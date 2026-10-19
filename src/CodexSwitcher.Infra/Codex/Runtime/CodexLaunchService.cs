using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Security.Hardening;

namespace CodexSwitcher.Infra.Codex.Runtime;

/// <summary>
/// Launches user-owned interactive Codex CLI or Desktop processes with a per-recipe CODEX_HOME.
/// ChatGPT auth is copied from the encrypted vault only while a process is running, the
/// account operation lock is held for that lifetime, and rotated credentials are written
/// back before the plaintext auth file is removed.
/// </summary>
public sealed class CodexLaunchService : ICodexLaunchService
{
    private const string ApiKeyEnvironmentVariable = "CODEX_SWITCHBOARD_API_KEY";
    private const string CodexSqliteHomeEnvironmentVariable = "CODEX_SQLITE_HOME";
    private const string DesktopUserDataEnvironmentVariable = "CODEX_ELECTRON_USER_DATA_PATH";
    private const string ManifestFileName = ".switchboard-launch.json";
    private const string SharedChatsManifestFileName = ".switchboard-shared-chats.json";
    private const uint SymbolicLinkFlagDirectory = 0x1;
    private const uint SymbolicLinkFlagAllowUnprivilegedCreate = 0x2;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorPrivilegeNotHeld = 1314;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private static readonly Regex TomlStringAssignmentPattern = new(
        @"^(?<key>[A-Za-z0-9_-]+)\s*=\s*""(?<value>(?:\\.|[^""])*)""\s*(?:#.*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TomlProfileSectionPattern = new(
        @"^\[profiles\.(?:""(?<quoted>(?:\\.|[^""])*)""|(?<bare>[^.\]]+))\]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly IProfileStore _profileStore;
    private readonly IApiProviderStore _providerStore;
    private readonly IVaultService _vault;
    private readonly IProfileOperationCoordinator _coordinator;
    private readonly IApiKeySecretStore _apiKeyStore;
    private readonly ICodexRuntimeResolver _runtimeResolver;
    private readonly ICodexRuntimeModelCatalogVerifier _runtimeModelCatalogVerifier;
    private readonly ICodexModelCatalogService _catalogService;
    private readonly ProfileService _profileService;
    private readonly IFileSystem _fs;
    private readonly ISwitchboardCodexProcessRegistry _processRegistry;
    private readonly object _sync = new();
    private readonly object _warningSync = new();
    private readonly Dictionary<Guid, SessionState> _sessions = [];
    private readonly HashSet<Guid> _launchingProfileIds = [];
    private readonly HashSet<Guid> _launchingChatGptProfileIds = [];
    private readonly List<string> _pendingWarnings = [];
    private Action<string>? _warningRaised;

    public event EventHandler? SessionsChanged;
    public event Action<string>? WarningRaised
    {
        add
        {
            if (value is null)
            {
                return;
            }

            string[] pending;
            lock (_warningSync)
            {
                _warningRaised += value;
                pending = _pendingWarnings.ToArray();
                _pendingWarnings.Clear();
            }

            // Recovery runs in the service constructor, before the UI can subscribe.
            // Replay those warnings on the first subscription instead of silently
            // losing the only explanation for a preserved/recovery-blocked session.
            foreach (var warning in pending)
            {
                try { value.Invoke(warning); }
                catch { }
            }
        }
        remove
        {
            lock (_warningSync)
            {
                _warningRaised -= value;
            }
        }
    }

    public CodexLaunchService(
        AppPaths paths,
        AppSettings settings,
        IProfileStore profileStore,
        IApiProviderStore providerStore,
        IVaultService vault,
        IProfileOperationCoordinator coordinator,
        IApiKeySecretStore apiKeyStore,
        ICodexRuntimeResolver runtimeResolver,
        ICodexRuntimeModelCatalogVerifier runtimeModelCatalogVerifier,
        ICodexModelCatalogService catalogService,
        ProfileService profileService,
        IFileSystem fileSystem,
        ISwitchboardCodexProcessRegistry processRegistry)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _providerStore = providerStore ?? throw new ArgumentNullException(nameof(providerStore));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _apiKeyStore = apiKeyStore ?? throw new ArgumentNullException(nameof(apiKeyStore));
        _runtimeResolver = runtimeResolver ?? throw new ArgumentNullException(nameof(runtimeResolver));
        _runtimeModelCatalogVerifier = runtimeModelCatalogVerifier ?? throw new ArgumentNullException(nameof(runtimeModelCatalogVerifier));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _processRegistry = processRegistry ?? throw new ArgumentNullException(nameof(processRegistry));

        RecoverPreviousSessions();
    }

    public IReadOnlyList<CodexLaunchSession> GetRunningSessions()
    {
        lock (_sync)
        {
            return _sessions.Values
                .Select(session => session.Presentation)
                .OrderBy(session => session.StartedAt)
                .ToList();
        }
    }

    public bool IsChatGptProfileInUse(Guid profileId)
    {
        lock (_sync)
        {
            return _launchingChatGptProfileIds.Contains(profileId) ||
                _sessions.Values.Any(session =>
                    session.Manifest.TargetKind == CodexLaunchTargetKind.ChatGptAccount &&
                    session.Manifest.CredentialProfileId == profileId);
        }
    }

    public async Task<CodexLaunchSession> LaunchAsync(
        CodexLaunchProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfile(profile);

        lock (_sync)
        {
            if (_launchingProfileIds.Contains(profile.Id) ||
                _sessions.Values.Any(session => session.Manifest.LaunchProfileId == profile.Id))
            {
                throw new InvalidOperationException($"'{profile.Name}' is already running or launching.");
            }

            if (profile.TargetKind == CodexLaunchTargetKind.ChatGptAccount &&
                (_launchingChatGptProfileIds.Contains(profile.CredentialProfileId) ||
                 _sessions.Values.Any(session =>
                     session.Manifest.TargetKind == CodexLaunchTargetKind.ChatGptAccount &&
                     session.Manifest.CredentialProfileId == profile.CredentialProfileId)))
            {
                throw new InvalidOperationException("This ChatGPT account already has a managed Codex session running or launching.");
            }

            _launchingProfileIds.Add(profile.Id);
            if (profile.TargetKind == CodexLaunchTargetKind.ChatGptAccount)
            {
                _launchingChatGptProfileIds.Add(profile.CredentialProfileId);
            }
        }

        try
        {
            return await LaunchCoreAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _launchingProfileIds.Remove(profile.Id);
                if (profile.TargetKind == CodexLaunchTargetKind.ChatGptAccount)
                {
                    _launchingChatGptProfileIds.Remove(profile.CredentialProfileId);
                }
            }
        }
    }

    private async Task<CodexLaunchSession> LaunchCoreAsync(
        CodexLaunchProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfile(profile);

        lock (_sync)
        {
            if (_sessions.Values.Any(session => session.Manifest.LaunchProfileId == profile.Id))
            {
                throw new InvalidOperationException($"'{profile.Name}' is already running.");
            }

            if (profile.TargetKind == CodexLaunchTargetKind.ChatGptAccount &&
                _sessions.Values.Any(session =>
                    session.Manifest.TargetKind == CodexLaunchTargetKind.ChatGptAccount &&
                    session.Manifest.CredentialProfileId == profile.CredentialProfileId))
            {
                throw new InvalidOperationException("This ChatGPT account already has a managed Codex session running.");
            }
        }

        var executable = profile.RuntimeKind == CodexLaunchRuntimeKind.Desktop
            ? ResolveDesktopExecutable()
            : ResolveExecutable();
        var arguments = ParseArguments(profile.Arguments);
        ValidateArguments(arguments);
        var workingDirectory = ResolveWorkingDirectory(profile.WorkingDirectory);
        var home = GetHomePath(profile);
        if (_fs.FileExists(GetManifestPath(home)) || _fs.FileExists(Path.Combine(home, "auth.json")))
        {
            throw new InvalidOperationException(
                "This launch profile has a prior session that still needs credential recovery. Its isolated home was preserved; restart Switchboard or resolve the saved session before launching again.");
        }

        _fs.CreateDirectory(home);
        try
        {
            DirectoryHardening.RestrictToCurrentUser(home);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IdentityNotMappedException or IOException)
        {
            throw new InvalidOperationException(
                "Switchboard could not secure this isolated launch profile for the current Windows user. The Codex session was not started.",
                ex);
        }

        if (profile.RuntimeKind == CodexLaunchRuntimeKind.Desktop)
        {
            _fs.CreateDirectory(Path.Combine(home, "user-data"));
        }

        IDisposable? profileLock = null;
        Process? startedProcess = null;
        SessionState? createdState = null;
        byte[]? authJson = null;
        string? apiKey = null;
        string? sharedSqliteHome = null;
        string? initialFingerprint = null;
        string credentialName;
        string? model = null;
        ApiProviderProfile? provider = null;
        CodexModelOverrides? modelOverrides = null;
        EffectiveToolPolicy? toolPolicy = null;
        string? catalogPath = null;
        LaunchManifest manifest = null!;
        try
        {
            if (profile.TargetKind == CodexLaunchTargetKind.ChatGptAccount)
            {
                profileLock = await _coordinator.LockAsync(profile.CredentialProfileId, cancellationToken).ConfigureAwait(false);
                var account = _profileStore.LoadAll().FirstOrDefault(item => item.Id == profile.CredentialProfileId)
                    ?? throw new InvalidOperationException("The selected ChatGPT account no longer exists.");
                if (account.IsActive)
                {
                    throw new InvalidOperationException(
                        "This account is currently active in the shared Codex home. Switch Switchboard to another account before launching an isolated CLI copy.");
                }

                if (string.Equals(account.AuthMode, "apikey", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("This saved account is not a ChatGPT OAuth profile.");
                }

                if (!_vault.Exists(account.Id))
                {
                    throw new InvalidOperationException("The selected ChatGPT account has no saved credential in the vault.");
                }

                authJson = _vault.LoadBlob(account.Id);
                var (authFile, accountClaims) = AuthJsonReader.Identify(authJson);
                if (authFile is null ||
                    string.Equals(authFile.AuthMode, "apikey", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The saved credential is not a valid ChatGPT OAuth profile. Re-authenticate this account in Switchboard before launching it.");
                }

                var identityMatches = !string.IsNullOrWhiteSpace(account.AccountSub)
                    ? string.Equals(account.AccountSub, accountClaims.Sub, StringComparison.Ordinal)
                    : !string.IsNullOrWhiteSpace(account.AccountEmail) &&
                      string.Equals(account.AccountEmail, accountClaims.Email, StringComparison.OrdinalIgnoreCase);
                if (!identityMatches)
                {
                    throw new InvalidOperationException(
                        "The saved OAuth credential no longer matches the selected ChatGPT account. Re-authenticate the account in Switchboard before launching it.");
                }

                initialFingerprint = Fingerprint.Compute(authJson);
                credentialName = account.DisplayName;
                if (!string.IsNullOrWhiteSpace(profile.Model))
                {
                    model = profile.Model;
                }
            }
            else
            {
                provider = _providerStore.GetById(profile.CredentialProfileId)
                    ?? throw new InvalidOperationException("The selected API provider no longer exists.");
                if (provider.Status != ApiProviderProfileStatus.Active)
                {
                    throw new InvalidOperationException("The selected API provider is not active.");
                }

                provider.TransportOverrides?.Validate();
                toolPolicy = EffectiveToolPolicy.Resolve(provider);

                apiKey = _apiKeyStore.GetApiKey(provider.Id);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException("The selected API provider has no saved API key.");
                }

                model = string.IsNullOrWhiteSpace(profile.Model)
                    ? provider.GetEffectiveSelectedModel()
                    : profile.Model;
                if (string.IsNullOrWhiteSpace(model))
                {
                    throw new InvalidOperationException("Choose an exact model for this API launch profile.");
                }

                if (provider.ModelInventory is not null)
                {
                    var inventoryItem = provider.ModelInventory.Models.FirstOrDefault(item =>
                        string.Equals(item.Slug, model, StringComparison.Ordinal));
                    if (inventoryItem is null || !inventoryItem.Enabled)
                    {
                        throw new InvalidOperationException("The exact model is not enabled in the selected API provider's inventory.");
                    }

                    modelOverrides = inventoryItem.UserOverrides ?? provider.ModelOverrides;
                    catalogPath = _catalogService.EnsureProfileModelCatalog(
                        provider,
                        model,
                        inventoryItem.UserOverrides?.ContextWindowTokens ?? inventoryItem.ContextWindow,
                        modelOverrides,
                        toolPolicy);
                }
                else
                {
                    if (!string.Equals(provider.GetEffectiveSelectedModel(), model, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The exact API model cannot be validated because this provider has no inventory. Select its current model or refresh its inventory first.");
                    }

                    modelOverrides = provider.ModelOverrides;
                    // Legacy API profiles may not have a persisted inventory yet.
                    // Still build an isolated catalog for the explicitly selected
                    // model; launching without one exposes Codex's unrelated
                    // built-in OpenAI model list in this isolated API session.
                    catalogPath = _catalogService.EnsureProfileModelCatalog(
                        provider,
                        model,
                        modelOverrides?.ContextWindowTokens,
                        modelOverrides,
                        toolPolicy);
                }

                if (string.IsNullOrWhiteSpace(catalogPath))
                {
                    throw new InvalidOperationException(
                        "Codex could not build a profile-specific model catalog for this API launch. Refresh the provider inventory or choose an enabled model before launching.");
                }

                credentialName = provider.DisplayName;
            }

            if (profile.ShareChatsWithMainCodex)
            {
                sharedSqliteHome = ResolveMainSqliteHome();
                PrepareSharedConversationEntries(home, sharedSqliteHome);
            }
            else
            {
                RemoveManagedSharedConversationEntries(home);
            }

            WriteHomeConfiguration(profile, provider, model, modelOverrides, catalogPath, toolPolicy, home, sharedSqliteHome);
            if (provider is not null && model is not null)
            {
                var expectedModels = provider.ModelInventory?.Models
                    .Where(item => item.Enabled)
                    .Select(item => item.Slug)
                    .ToHashSet(StringComparer.Ordinal)
                    ?? new HashSet<string>(StringComparer.Ordinal);
                expectedModels.Add(model);

                var verification = await _runtimeModelCatalogVerifier.VerifyAtHomeAsync(
                    home,
                    provider.StableCodexProviderId,
                    model,
                    expectedModels,
                    cancellationToken).ConfigureAwait(false);
                if (!verification.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"The isolated API launch profile did not pass Codex runtime verification: {verification.FailureReason ?? "the exact provider/model catalog was not available"}");
                }
            }

            var sessionId = Guid.NewGuid();
            manifest = new LaunchManifest
            {
                SessionId = sessionId,
                LaunchProfileId = profile.Id,
                TargetKind = profile.TargetKind,
                RuntimeKind = profile.RuntimeKind,
                CredentialProfileId = profile.CredentialProfileId,
                ProfileName = profile.Name.Trim(),
                CredentialName = credentialName,
                Model = model,
                WorkingDirectory = workingDirectory,
                ProcessId = 0,
                ProcessStartedAt = DateTimeOffset.MinValue,
                ProcessStartMayHaveBeenIssued = false,
                InitialAuthFingerprint = initialFingerprint,
            };

            // Persist recovery metadata before placing a plaintext OAuth copy in an
            // isolated CODEX_HOME. The false marker proves startup can safely recover
            // this home if the app exits before it records start intent.
            _fs.WriteAllTextAtomic(GetManifestPath(home), JsonSerializer.Serialize(manifest, JsonOptions));
            if (authJson is not null)
            {
                _fs.WriteAllBytesAtomic(Path.Combine(home, "auth.json"), authJson);
            }

            // A zero-PID manifest is recoverable only while this remains false.
            // From this durable marker onward, a crash may leave an untracked child.
            manifest.ProcessStartMayHaveBeenIssued = true;
            _fs.WriteAllTextAtomic(GetManifestPath(home), JsonSerializer.Serialize(manifest, JsonOptions));
            using (await _processRegistry.AcquireProcessLifecycleLeaseAsync(cancellationToken).ConfigureAwait(false))
            {
                startedProcess = StartCodexProcess(
                    executable,
                    arguments,
                    workingDirectory,
                    home,
                    apiKey,
                    profile.RuntimeKind,
                    sharedSqliteHome);
                _processRegistry.RegisterOwnedProcess(startedProcess);
            }
            manifest.ProcessId = startedProcess.Id;
            _fs.WriteAllTextAtomic(GetManifestPath(home), JsonSerializer.Serialize(manifest, JsonOptions));
            manifest.ProcessStartedAt = TryGetStartTime(startedProcess)
                ?? throw new InvalidOperationException("Switchboard could not verify the started Codex process timestamp; the isolated session was rolled back.");
            var presentation = ToSession(manifest);
            var state = new SessionState(startedProcess, home, manifest, presentation, profileLock);
            createdState = state;

            _fs.WriteAllTextAtomic(GetManifestPath(home), JsonSerializer.Serialize(manifest, JsonOptions));
            lock (_sync)
            {
                _sessions.Add(sessionId, state);
            }

            profileLock = null;
            WatchProcess(state);
            startedProcess = null;
            if (authJson is not null)
            {
                CryptographicOperations.ZeroMemory(authJson);
                authJson = null;
            }

            PublishSessionsChanged();
            return presentation;
        }
        catch
        {
            if (startedProcess is not null)
            {
                var processStopped = false;
                try
                {
                    if (startedProcess.HasExited)
                    {
                        processStopped = true;
                    }
                    else
                    {
                        startedProcess.Kill(entireProcessTree: true);
                        await startedProcess.WaitForExitAsync(CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                            .ConfigureAwait(false);
                        processStopped = startedProcess.HasExited;
                    }
                }
                catch
                {
                    try { processStopped = startedProcess.HasExited; }
                    catch { processStopped = false; }
                }

                if (!processStopped)
                {
                    manifest.ProcessId = startedProcess.Id;
                    manifest.ProcessStartedAt = TryGetStartTime(startedProcess) ?? manifest.ProcessStartedAt;
                    try
                    {
                        _fs.WriteAllTextAtomic(GetManifestPath(home), JsonSerializer.Serialize(manifest, JsonOptions));
                    }
                    catch
                    {
                        // The pre-start marker and isolated credential are still
                        // preserved even if refreshing recovery metadata fails.
                    }

                    try
                    {
                        using (await _processRegistry.AcquireProcessLifecycleLeaseAsync(CancellationToken.None).ConfigureAwait(false))
                        {
                            _processRegistry.RegisterOwnedProcess(startedProcess);
                        }
                    }
                    catch { }

                    var retainedState = createdState ?? new SessionState(
                        startedProcess,
                        home,
                        manifest,
                        ToSession(manifest, DateTimeOffset.UtcNow),
                        profileLock);
                    lock (_sync)
                    {
                        _sessions[manifest.SessionId] = retainedState;
                    }

                    createdState = retainedState;
                    profileLock = null;
                    try { WatchProcess(retainedState); }
                    catch { }

                    PublishSessionsChanged();
                    PublishWarning(
                        $"Codex did not stop after a failed launch of '{profile.Name}'. Its isolated session and credential recovery files were preserved; stop it from Launch Profiles before relaunching.");
                }
                else
                {
                    if (createdState is not null && profileLock is null)
                    {
                        try
                        {
                            await CompleteSessionAsync(createdState).ConfigureAwait(false);
                        }
                        catch { }
                    }
                    else
                    {
                        startedProcess.Dispose();
                        _fs.Delete(Path.Combine(home, "auth.json"));
                        _fs.Delete(GetManifestPath(home));
                    }
                }
            }
            else if (createdState is null)
            {
                // No CLI process owns this freshly seeded home, so discard only the
                // plaintext auth copy created by this failed launch attempt.
                _fs.Delete(Path.Combine(home, "auth.json"));
                _fs.Delete(GetManifestPath(home));
            }

            if (authJson is not null)
            {
                CryptographicOperations.ZeroMemory(authJson);
            }

            profileLock?.Dispose();
            throw;
        }
        finally
        {
            apiKey = null;
        }
    }

    public async Task StopAsync(Guid launchProfileId, CancellationToken cancellationToken = default)
    {
        SessionState? state;
        lock (_sync)
        {
            state = _sessions.Values.FirstOrDefault(session => session.Manifest.LaunchProfileId == launchProfileId);
        }

        if (state is null)
        {
            return;
        }

        try
        {
            if (!HasExitedOrDetached(state.Process))
            {
                try
                {
                    state.Process.CloseMainWindow();
                    using var gracefulTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    gracefulTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await WaitForExitOrDetachedAsync(state.Process, gracefulTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Fall through to terminating only the process tree started for this profile.
                }
                catch (InvalidOperationException)
                {
                    // It exited between the initial check and CloseMainWindow.
                }

                if (!HasExitedOrDetached(state.Process))
                {
                    state.Process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Windows could not stop the Codex process started by Switchboard.", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await WaitForExitOrDetachedAsync(state.Process, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Codex did not exit after Switchboard stopped its owned process tree. Credential write-back was left pending.");
        }

        await CompleteSessionAsync(state).ConfigureAwait(false);
    }

    private static bool HasExitedOrDetached(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // The Exited watcher completes the session and disposes this Process
            // object. A detached/disposed process therefore means it has already
            // exited, not that stopping the owned session failed.
            return true;
        }
    }

    private static async Task WaitForExitOrDetachedAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Completion may race the stop request and dispose the Process after
            // the child exits. CompleteSessionAsync below serializes write-back.
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        Guid[] launchProfileIds;
        lock (_sync)
        {
            launchProfileIds = _sessions.Values
                .Select(session => session.Manifest.LaunchProfileId)
                .Distinct()
                .ToArray();
        }

        await Task.WhenAll(launchProfileIds.Select(profileId => StopAsync(profileId, cancellationToken)))
            .ConfigureAwait(false);
    }

    private void RecoverPreviousSessions()
    {
        if (!Directory.Exists(_paths.LaunchHomesDir))
        {
            return;
        }

        foreach (var home in Directory.EnumerateDirectories(_paths.LaunchHomesDir))
        {
            var manifestPath = GetManifestPath(home);
            if (!_fs.FileExists(manifestPath))
            {
                continue;
            }

            LaunchManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<LaunchManifest>(_fs.ReadAllText(manifestPath), JsonOptions);
            }
            catch
            {
                PublishWarning($"A saved Codex launch session record could not be read: {manifestPath}");
                continue;
            }

            if (manifest is null)
            {
                continue;
            }

            manifest.RuntimeKind = ResolveRuntimeKind(manifest, home);
            var credentialHomeSecured =
                manifest.TargetKind != CodexLaunchTargetKind.ChatGptAccount ||
                TrySecureCredentialHome(home, manifest.ProfileName);

            if (manifest.ProcessId <= 0)
            {
                if (manifest.ProcessStartMayHaveBeenIssued == false)
                {
                    // The durable launch-attempt marker proves Process.Start was
                    // not reached, so normal credential recovery is safe.
                    if (credentialHomeSecured)
                    {
                        RecoverExitedSession(home, manifest);
                    }
                    continue;
                }

                // The child may have started just before Switchboard stopped but
                // before its PID was atomically written to the manifest. Do not
                // treat a true/missing legacy marker as proof it is safe to remove auth.json.
                PublishWarning(
                    $"Could not verify whether managed Codex session '{manifest.ProfileName}' started before Switchboard stopped. Its isolated home and credential recovery files were preserved; resolve this session before launching it again.");
                continue;
            }

            if (manifest.ProcessStartedAt == default)
            {
                PublishWarning(
                    $"Could not verify the start time of managed Codex session '{manifest.ProfileName}'. Its isolated home and credential recovery files were preserved; resolve this session before launching it again.");
                continue;
            }

            Process? process = null;
            var matchingProcessIsRunning = false;
            var processStateUnknown = false;
            try
            {
                process = Process.GetProcessById(manifest.ProcessId);
                var actualStart = TryGetStartTime(process);
                if (actualStart is null)
                {
                    processStateUnknown = true;
                }
                else if (Math.Abs((actualStart.Value - manifest.ProcessStartedAt).TotalSeconds) >= 2)
                {
                    processStateUnknown = true;
                }
                else if (!process.HasExited)
                {
                    matchingProcessIsRunning = true;
                    IDisposable? profileLock = manifest.TargetKind == CodexLaunchTargetKind.ChatGptAccount
                        ? _coordinator.Lock(manifest.CredentialProfileId)
                        : null;
                    var state = new SessionState(process, home, manifest, ToSession(manifest), profileLock);
                    lock (_sync)
                    {
                        _sessions[manifest.SessionId] = state;
                    }

                    using (_processRegistry.AcquireProcessLifecycleLease())
                    {
                        _processRegistry.RegisterOwnedProcess(process);
                    }
                    WatchProcess(state);
                    process = null;
                    continue;
                }
            }
            catch (ArgumentException)
            {
                // The process ended while Switchboard was not running.
            }
            catch (InvalidOperationException)
            {
                // A process object exists, but its state could not be verified.
                processStateUnknown = true;
            }
            catch (Exception ex)
            {
                processStateUnknown = true;
                System.Diagnostics.Debug.WriteLine(
                    $"[LaunchRecovery] Process verification failed for session {manifest.SessionId}: {ex.GetType().Name}");
            }
            finally
            {
                process?.Dispose();
            }

            if (processStateUnknown)
            {
                PublishWarning(
                    $"Could not verify whether managed Codex session '{manifest.ProfileName}' is still running. Its isolated home and credential recovery files were preserved; resolve this session before launching it again.");
                continue;
            }

            if (!matchingProcessIsRunning)
            {
                if (credentialHomeSecured)
                {
                    RecoverExitedSession(home, manifest);
                }
            }
        }
    }

    private bool TrySecureCredentialHome(string home, string profileName)
    {
        try
        {
            DirectoryHardening.RestrictToCurrentUser(home);
            return true;
        }
        catch (Exception ex)
        {
            PublishWarning(
                $"Could not secure the isolated credential home for '{profileName}'. Its auth file and recovery record were preserved; fix the folder permissions before resolving this session. {ex.Message}");
            return false;
        }
    }

    private void RecoverExitedSession(string home, LaunchManifest manifest)
    {
        if (manifest.TargetKind != CodexLaunchTargetKind.ChatGptAccount)
        {
            _fs.Delete(GetManifestPath(home));
            return;
        }

        IDisposable? profileLock = null;
        try
        {
            profileLock = _coordinator.Lock(manifest.CredentialProfileId);
            var authPath = Path.Combine(home, "auth.json");
            if (_fs.FileExists(authPath))
            {
                var bytes = _fs.ReadAllBytes(authPath);
                try
                {
                    var changed = !string.Equals(
                        Fingerprint.Compute(bytes),
                        manifest.InitialAuthFingerprint,
                        StringComparison.OrdinalIgnoreCase);
                    if (changed && !string.IsNullOrWhiteSpace(manifest.InitialAuthFingerprint))
                    {
                        if (!_profileService.PersistIsolatedLaunchRefreshUnderLock(
                                manifest.CredentialProfileId,
                                manifest.InitialAuthFingerprint,
                                bytes))
                        {
                            PublishWarning(
                                $"A stopped Codex session refreshed '{manifest.CredentialName}', but its credential had changed in Switchboard. The isolated auth file was preserved at {authPath}.");
                            return;
                        }
                    }

                    _fs.Delete(authPath);
                    _fs.Delete(GetManifestPath(home));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            else
            {
                _fs.Delete(GetManifestPath(home));
            }
        }
        catch (Exception ex)
        {
            PublishWarning($"Could not recover a stopped Codex session credential: {ex.Message}");
        }
        finally
        {
            profileLock?.Dispose();
        }
    }

    private void WatchProcess(SessionState state)
    {
        if (state.WatcherAttached)
        {
            return;
        }

        state.Process.EnableRaisingEvents = true;
        state.Process.Exited += (_, _) => _ = CompleteSessionAsync(state);
        state.WatcherAttached = true;
        if (state.Process.HasExited)
        {
            _ = CompleteSessionAsync(state);
        }
    }

    private async Task CompleteSessionAsync(SessionState state)
    {
        await state.CompletionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (state.IsCompleted)
            {
                return;
            }

            var authPath = Path.Combine(state.Home, "auth.json");
            var manifestPath = GetManifestPath(state.Home);
            var keepRecoveryData = false;
            if (state.ProfileLock is not null && _fs.FileExists(authPath))
            {
                keepRecoveryData = !TrySecureCredentialHome(state.Home, state.Manifest.ProfileName);
                byte[]? bytes = null;
                try
                {
                    if (!keepRecoveryData)
                    {
                        bytes = _fs.ReadAllBytes(authPath);
                        var currentFingerprint = Fingerprint.Compute(bytes);
                        if (!string.Equals(
                                currentFingerprint,
                                state.Manifest.InitialAuthFingerprint,
                                StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(state.Manifest.InitialAuthFingerprint))
                        {
                            if (!_profileService.PersistIsolatedLaunchRefreshUnderLock(
                                    state.Manifest.CredentialProfileId,
                                    state.Manifest.InitialAuthFingerprint,
                                    bytes))
                            {
                                keepRecoveryData = true;
                                PublishWarning(
                                    $"Codex refreshed '{state.Manifest.CredentialName}', but Switchboard could not safely write the new credential back. The auth file was preserved at {authPath}.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    keepRecoveryData = true;
                    PublishWarning($"Codex stopped, but its refreshed credential could not be saved: {ex.Message}");
                }
                finally
                {
                    if (bytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }
            }

            if (!keepRecoveryData)
            {
                _fs.Delete(authPath);
                _fs.Delete(manifestPath);
            }

            state.ProfileLock?.Dispose();
            state.ProfileLock = null;
            _processRegistry.UnregisterOwnedProcess(state.Manifest.ProcessId);
            lock (_sync)
            {
                _sessions.Remove(state.Manifest.SessionId);
            }

            state.IsCompleted = true;
            state.Process.Dispose();
            PublishSessionsChanged();
        }
        finally
        {
            state.CompletionGate.Release();
        }
    }

    private string ResolveExecutable()
    {
        var runtime = _runtimeResolver.ResolveCurrentRuntime(_settings.CodexExecutablePathOverride);
        if (string.IsNullOrWhiteSpace(runtime.ExecutablePath) || !File.Exists(runtime.ExecutablePath))
        {
            throw new InvalidOperationException("Codex CLI was not found. Install it or select a valid executable in Settings.");
        }

        return runtime.ExecutablePath;
    }

    private static string ResolveDesktopExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Codex Desktop launch profiles are available only on Windows.");
        }

        // The MSIX package's app/ChatGPT.exe is the real entry point. Codex.exe is an
        // updater trampoline and may fail when started directly. Query the registered
        // package each time so an app update can move its versioned WindowsApps path.
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShellPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("(Get-AppxPackage -Name 'OpenAI.Codex' | Select-Object -First 1 -ExpandProperty InstallLocation)");

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows could not query the installed Codex Desktop package.");
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch { }
                throw new InvalidOperationException("Timed out while locating the installed Codex Desktop package.");
            }

            var installLocation = output.GetAwaiter().GetResult().Trim();
            var executable = Path.Combine(installLocation, "app", "ChatGPT.exe");
            if (string.IsNullOrWhiteSpace(installLocation) || !File.Exists(executable))
            {
                throw new InvalidOperationException("Codex Desktop was not found. Install the OpenAI.Codex Microsoft Store app first.");
            }

            return executable;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Could not start PowerShell to locate Codex Desktop.", ex);
        }
    }

    private static Process StartCodexProcess(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string codexHome,
        string? apiKey,
        CodexLaunchRuntimeKind runtimeKind,
        string? sharedSqliteHome)
    {
        var extension = Path.GetExtension(executable);
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = workingDirectory,
        };
        startInfo.Environment["CODEX_HOME"] = codexHome;
        if (string.IsNullOrWhiteSpace(sharedSqliteHome))
        {
            startInfo.Environment.Remove(CodexSqliteHomeEnvironmentVariable);
        }
        else
        {
            startInfo.Environment[CodexSqliteHomeEnvironmentVariable] = sharedSqliteHome;
        }

        startInfo.Environment.Remove(DesktopUserDataEnvironmentVariable);
        startInfo.Environment.Remove(ApiKeyEnvironmentVariable);
        if (runtimeKind == CodexLaunchRuntimeKind.Desktop)
        {
            // The installed Desktop runtime resolves its per-instance userData
            // before this environment override is applied and logs it as a late
            // path change. Use the launch-time Chromium switch as the single
            // profile-isolation source; the inherited override was removed above.
            startInfo.ArgumentList.Add($"--user-data-dir={Path.Combine(codexHome, "user-data")}");
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            startInfo.Environment[ApiKeyEnvironmentVariable] = apiKey;
        }

        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = ResolvePowerShellPath();
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");

            var payload = JsonSerializer.Serialize(new
            {
                executable,
                workingDirectory,
                arguments,
            });
            var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
            var script = $"$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{payloadBase64}')); $d=ConvertFrom-Json -InputObject $p; Set-Location -LiteralPath $d.workingDirectory; $a=@($d.arguments); & $d.executable @a; exit $LASTEXITCODE";
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        }
        else
        {
            startInfo.FileName = executable;
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Windows did not start {(runtimeKind == CodexLaunchRuntimeKind.Desktop ? "Codex Desktop" : "Codex CLI")}.");
            }

            // The child process has its own copy of the environment block now. Do
            // not keep the plaintext key reachable through Process.StartInfo for
            // the lifetime of the managed session.
            startInfo.Environment.Remove(ApiKeyEnvironmentVariable);
            return process;
        }
        catch
        {
            startInfo.Environment.Remove(ApiKeyEnvironmentVariable);
            process.Dispose();
            throw;
        }
    }

    private static string ResolvePowerShellPath()
    {
        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(windowsPowerShell))
        {
            return windowsPowerShell;
        }

        return "pwsh.exe";
    }

    private void WriteHomeConfiguration(
        CodexLaunchProfile profile,
        ApiProviderProfile? provider,
        string? model,
        CodexModelOverrides? modelOverrides,
        string? catalogPath,
        EffectiveToolPolicy? toolPolicy,
        string home,
        string? sharedSqliteHome)
    {
        var lines = new List<string> { "cli_auth_credentials_store = \"file\"" };
        if (!string.IsNullOrWhiteSpace(sharedSqliteHome))
        {
            lines.Add($"sqlite_home = {TomlString(sharedSqliteHome)}");
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            lines.Add($"model = {TomlString(model)}");
        }

        if (provider is not null)
        {
            lines.Add($"model_provider = {TomlString(provider.StableCodexProviderId)}");
            if (!string.IsNullOrWhiteSpace(catalogPath))
            {
                lines.Add($"model_catalog_json = {TomlString(catalogPath)}");
            }

            if (modelOverrides?.ContextWindowTokens is { } contextWindow)
            {
                lines.Add($"model_context_window = {contextWindow.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (modelOverrides?.AutoCompactTokenLimit is { } compactLimit)
            {
                lines.Add($"model_auto_compact_token_limit = {compactLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (modelOverrides?.AutoCompactTokenLimitScope is { } compactScope && compactScope != CompactLimitScope.Default)
            {
                var scope = compactScope == CompactLimitScope.BodyAfterPrefix ? "body_after_prefix" : "total";
                lines.Add($"model_auto_compact_token_limit_scope = {TomlString(scope)}");
            }
            if (modelOverrides?.ReasoningEffort is { } selectedEffort && selectedEffort != CodexReasoningEffort.Default)
            {
                lines.Add($"model_reasoning_effort = {TomlString(selectedEffort.ToString().ToLowerInvariant())}");
            }
            if (modelOverrides?.ReasoningSummary is { } selectedSummary && selectedSummary != CodexReasoningSummary.Default)
            {
                lines.Add($"model_reasoning_summary = {TomlString(selectedSummary.ToString().ToLowerInvariant())}");
            }
            if (modelOverrides?.Verbosity is { } selectedVerbosity && selectedVerbosity != CodexVerbosity.Default)
            {
                lines.Add($"model_verbosity = {TomlString(selectedVerbosity.ToString().ToLowerInvariant())}");
            }
            if (modelOverrides?.ToolOutputTokenLimit is { } outputLimit)
            {
                lines.Add($"tool_output_token_limit = {outputLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            if (toolPolicy?.AllowHostedWebSearch == false)
            {
                lines.Add("web_search = \"disabled\"");
            }

            lines.Add(string.Empty);
            lines.Add($"[model_providers.{provider.StableCodexProviderId}]");
            lines.Add($"name = {TomlString(provider.DisplayName)}");
            lines.Add($"base_url = {TomlString(provider.BaseUrl)}");
            lines.Add($"wire_api = {TomlString(string.IsNullOrWhiteSpace(provider.WireApi) ? "responses" : provider.WireApi)}");
            lines.Add($"env_key = {TomlString(ApiKeyEnvironmentVariable)}");

            var transport = provider.TransportOverrides;
            if (transport?.RequestMaxRetries is { } requestRetries)
            {
                lines.Add($"request_max_retries = {requestRetries.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (transport?.StreamMaxRetries is { } streamRetries)
            {
                lines.Add($"stream_max_retries = {streamRetries.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (transport?.StreamIdleTimeoutMs is { } idleTimeout)
            {
                lines.Add($"stream_idle_timeout_ms = {idleTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (transport?.WebSocketConnectTimeoutMs is { } websocketTimeout)
            {
                lines.Add($"websocket_connect_timeout_ms = {websocketTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (transport?.SupportsWebSockets is { } supportsWebSockets)
            {
                lines.Add($"supports_websockets = {supportsWebSockets.ToString().ToLowerInvariant()}");
            }
            if (toolPolicy?.AllowStandaloneWebSearch == true && transport?.SupportsStandaloneWebSearch == true)
            {
                lines.Add("supports_standalone_web_search = true");
            }

            AppendTomlMap(lines, $"model_providers.{provider.StableCodexProviderId}.query_params", transport?.QueryParams);
            AppendTomlMap(lines, $"model_providers.{provider.StableCodexProviderId}.http_headers", transport?.HttpHeaders);
            AppendTomlMap(lines, $"model_providers.{provider.StableCodexProviderId}.env_http_headers", transport?.EnvHttpHeaders);
        }

        _fs.WriteAllTextAtomic(Path.Combine(home, "config.toml"), string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private string ResolveMainSqliteHome()
    {
        try
        {
            var configuredSqliteHome = ReadConfiguredMainSqliteHome();
            var environmentSqliteHome = Environment.GetEnvironmentVariable(CodexSqliteHomeEnvironmentVariable);
            var selectedPath = configuredSqliteHome ?? environmentSqliteHome ?? _paths.Codex.CodexHome;
            if (string.IsNullOrWhiteSpace(selectedPath) || !Path.IsPathFullyQualified(selectedPath))
            {
                throw new InvalidOperationException("The main Codex SQLite home is not an absolute path.");
            }

            return NormalizePath(selectedPath);
        }
        catch (Exception ex) when (ex is not CodexLaunchProfileSharingException)
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.MainStoragePathUnresolved,
                "Switchboard could not safely resolve the main Codex SQLite storage path.",
                ex);
        }
    }

    private string? ReadConfiguredMainSqliteHome()
    {
        var configPath = _paths.Codex.ConfigTomlPath;
        if (!_fs.FileExists(configPath))
        {
            return null;
        }

        var rootProfile = (string?)null;
        var rootSqliteHome = (string?)null;
        var profileSqliteHomes = new Dictionary<string, string>(StringComparer.Ordinal);
        var atRoot = true;
        string? currentProfile = null;
        foreach (var rawLine in _fs.ReadAllText(configPath).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                atRoot = false;
                currentProfile = TryParseProfileTableName(line);
                continue;
            }

            if (atRoot)
            {
                if (TryReadTomlString(line, "profile", out var profileName))
                {
                    rootProfile = profileName;
                }
                else if (TryReadTomlString(line, "sqlite_home", out var sqliteHome))
                {
                    rootSqliteHome = sqliteHome;
                }
            }
            else if (currentProfile is not null && TryReadTomlString(line, "sqlite_home", out var profileHome))
            {
                profileSqliteHomes[currentProfile] = profileHome;
            }
        }

        if (rootProfile is not null && profileSqliteHomes.TryGetValue(rootProfile, out var activeProfileSqliteHome))
        {
            return activeProfileSqliteHome;
        }

        return rootSqliteHome;
    }

    private static string? TryParseProfileTableName(string line)
    {
        var match = TomlProfileSectionPattern.Match(line);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["quoted"].Success
            ? CodexRoutingConfigStore.TomlUnescape(match.Groups["quoted"].Value)
            : match.Groups["bare"].Value.Trim();
    }

    private static bool TryReadTomlString(string line, string key, out string value)
    {
        var match = TomlStringAssignmentPattern.Match(line);
        if (match.Success && string.Equals(match.Groups["key"].Value, key, StringComparison.Ordinal))
        {
            value = CodexRoutingConfigStore.TomlUnescape(match.Groups["value"].Value);
            return true;
        }

        value = string.Empty;
        return false;
    }

    private void PrepareSharedConversationEntries(string launchHome, string sqliteHome)
    {
        var mainHome = NormalizePath(_paths.Codex.CodexHome);
        var normalizedLaunchHome = NormalizePath(launchHome);
        var normalizedSqliteHome = NormalizePath(sqliteHome);
        if (PathsOverlap(mainHome, normalizedLaunchHome) || PathsOverlap(normalizedSqliteHome, normalizedLaunchHome))
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.MainStoragePathUnresolved,
                "The main Codex home overlaps this isolated launch profile; conversation sharing was not enabled.");
        }

        if (!Directory.Exists(mainHome))
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.MainStoragePathUnresolved,
                "The main Codex home does not exist, so Switchboard cannot share its conversations.");
        }

        RemoveManagedSharedConversationEntries(launchHome);

        var sessionsLink = Path.Combine(launchHome, "sessions");
        var archivedSessionsLink = Path.Combine(launchHome, "archived_sessions");
        var sessionIndexLink = Path.Combine(launchHome, "session_index.jsonl");
        ValidateShareLinkPath(sessionsLink, isDirectory: true);
        ValidateShareLinkPath(archivedSessionsLink, isDirectory: true);
        ValidateShareLinkPath(sessionIndexLink, isDirectory: false);

        var sessionsTarget = Path.Combine(mainHome, "sessions");
        var archivedSessionsTarget = Path.Combine(mainHome, "archived_sessions");
        var sessionIndexTarget = Path.Combine(mainHome, "session_index.jsonl");
        Directory.CreateDirectory(sessionsTarget);
        Directory.CreateDirectory(archivedSessionsTarget);
        EnsureEmptyFileExists(sessionIndexTarget);

        var manifest = new SharedChatsManifest
        {
            Version = 2,
            CodexHome = mainHome,
            SqliteHome = normalizedSqliteHome,
            OwnsSessionsLink = true,
            OwnsArchivedSessionsLink = true,
            OwnsSessionIndexLink = true,
        };
        var manifestPath = GetSharedChatsManifestPath(launchHome);
        _fs.WriteAllTextAtomic(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));

        try
        {
            EnsureSharedLink(sessionsLink, sessionsTarget, isDirectory: true);
            EnsureSharedLink(archivedSessionsLink, archivedSessionsTarget, isDirectory: true);
            EnsureSharedLink(sessionIndexLink, sessionIndexTarget, isDirectory: false);
        }
        catch
        {
            try
            {
                RemoveManagedSharedConversationEntries(launchHome);
            }
            catch
            {
                // Keep the ownership marker if rollback is incomplete so the
                // next launch can safely retry removing only these exact links.
            }

            throw;
        }
    }

    private void RemoveManagedSharedConversationEntries(string launchHome)
    {
        var manifestPath = GetSharedChatsManifestPath(launchHome);
        if (!_fs.FileExists(manifestPath))
        {
            return;
        }

        SharedChatsManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SharedChatsManifest>(_fs.ReadAllText(manifestPath), JsonOptions);
        }
        catch (Exception ex)
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.ExistingProfileData,
                "The shared-chat recovery marker is unreadable; Switchboard preserved the profile unchanged.",
                ex);
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.CodexHome) || !Path.IsPathFullyQualified(manifest.CodexHome))
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.ExistingProfileData,
                "The shared-chat recovery marker is invalid; Switchboard preserved the profile unchanged.");
        }

        if (manifest.Version < 2)
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.ExistingProfileData,
                "The shared-chat recovery marker uses an older format; Switchboard preserved the profile unchanged.");
        }

        var previousMainHome = NormalizePath(manifest.CodexHome);
        if (manifest.OwnsSessionsLink)
        {
            RemoveSharedLink(Path.Combine(launchHome, "sessions"), Path.Combine(previousMainHome, "sessions"), isDirectory: true);
        }

        if (manifest.OwnsArchivedSessionsLink)
        {
            RemoveSharedLink(Path.Combine(launchHome, "archived_sessions"), Path.Combine(previousMainHome, "archived_sessions"), isDirectory: true);
        }

        if (manifest.OwnsSessionIndexLink)
        {
            RemoveSharedLink(Path.Combine(launchHome, "session_index.jsonl"), Path.Combine(previousMainHome, "session_index.jsonl"), isDirectory: false);
        }

        _fs.Delete(manifestPath);
    }

    private static void ValidateShareLinkPath(string linkPath, bool isDirectory)
    {
        if (!TryGetAttributes(linkPath, out var attributes))
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw ExistingProfileDataException(linkPath);
        }

        var isExistingDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isExistingDirectory != isDirectory)
        {
            throw ExistingProfileDataException(linkPath);
        }

        if (isDirectory
            ? Directory.EnumerateFileSystemEntries(linkPath).Any()
            : new FileInfo(linkPath).Length > 0)
        {
            throw ExistingProfileDataException(linkPath);
        }
    }

    private static void EnsureSharedLink(string linkPath, string targetPath, bool isDirectory)
    {
        if (TryGetAttributes(linkPath, out var attributes))
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Only remove links recorded by our manifest. Accepting an
                // unmarked matching link would make a later opt-out unlink a
                // user-created junction that Switchboard does not own.
                throw ExistingProfileDataException(linkPath);
            }

            var isExistingDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isExistingDirectory != isDirectory)
            {
                throw ExistingProfileDataException(linkPath);
            }

            if (isDirectory)
            {
                if (Directory.EnumerateFileSystemEntries(linkPath).Any())
                {
                    throw ExistingProfileDataException(linkPath);
                }

                Directory.Delete(linkPath, recursive: false);
            }
            else
            {
                if (new FileInfo(linkPath).Length > 0)
                {
                    throw ExistingProfileDataException(linkPath);
                }

                File.Delete(linkPath);
            }
        }

        CreateSharedSymbolicLink(linkPath, targetPath, isDirectory);
    }

    private static void RemoveSharedLink(string linkPath, string expectedTarget, bool isDirectory)
    {
        if (!TryGetAttributes(linkPath, out var attributes) || (attributes & FileAttributes.ReparsePoint) == 0)
        {
            return;
        }

        if (!LinkTargetMatches(linkPath, expectedTarget, isDirectory))
        {
            throw ExistingProfileDataException(linkPath);
        }

        if (isDirectory)
        {
            Directory.Delete(linkPath, recursive: false);
        }
        else
        {
            File.Delete(linkPath);
        }
    }

    private static void EnsureEmptyFileExists(string path)
    {
        if (Directory.Exists(path))
        {
            throw new IOException($"Expected a Codex history file but found a directory at '{path}'.");
        }

        if (File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another Codex process created the file while this profile was prepared.
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }

        attributes = default;
        return false;
    }

    private static bool LinkTargetMatches(string linkPath, string expectedTarget, bool isDirectory)
    {
        try
        {
            FileSystemInfo link = isDirectory ? new DirectoryInfo(linkPath) : new FileInfo(linkPath);
            var target = link.LinkTarget;
            return !string.IsNullOrWhiteSpace(target) &&
                PathsEqual(Path.GetFullPath(target, Path.GetDirectoryName(linkPath)!), expectedTarget);
        }
        catch
        {
            return false;
        }
    }

    private static void CreateSharedSymbolicLink(string linkPath, string targetPath, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.WindowsLinkPermission,
                "Conversation sharing is currently supported only on Windows.");
        }

        var flags = (isDirectory ? SymbolicLinkFlagDirectory : 0) | SymbolicLinkFlagAllowUnprivilegedCreate;
        if (CreateSymbolicLinkW(linkPath, targetPath, flags))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorInvalidParameter && CreateSymbolicLinkW(
                linkPath,
                targetPath,
                isDirectory ? SymbolicLinkFlagDirectory : 0))
        {
            return;
        }

        error = Marshal.GetLastWin32Error();
        if (error == ErrorAlreadyExists && LinkTargetMatches(linkPath, targetPath, isDirectory))
        {
            return;
        }

        var win32Exception = new Win32Exception(error);
        if (error is ErrorPrivilegeNotHeld or 5)
        {
            throw new CodexLaunchProfileSharingException(
                CodexLaunchProfileSharingFailureReason.WindowsLinkPermission,
                "Windows did not allow the required conversation-history links to be created.",
                win32Exception);
        }

        throw new IOException($"Could not link '{linkPath}' to the shared Codex history path.", win32Exception);
    }

    private static CodexLaunchProfileSharingException ExistingProfileDataException(string path) => new(
        CodexLaunchProfileSharingFailureReason.ExistingProfileData,
        $"This launch profile already contains local Codex data at '{path}'. Switchboard did not replace or merge it.");

    private static bool PathsOverlap(string first, string second) =>
        PathsEqual(first, second) || IsPathWithin(first, second) || IsPathWithin(second, first);

    private static bool IsPathWithin(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return !Path.IsPathRooted(relative) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(NormalizePath(first), NormalizePath(second), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string GetSharedChatsManifestPath(string home) => Path.Combine(home, SharedChatsManifestFileName);

    private static bool CreateSymbolicLinkW(string linkPath, string targetPath, uint flags) =>
        CreateSymbolicLinkNative(linkPath, targetPath, flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateSymbolicLinkNative(string linkPath, string targetPath, uint flags);

    private static string TomlString(string value)
        => $"\"{CodexRoutingConfigStore.TomlEscape(value)}\"";

    private static void AppendTomlMap(
        List<string> lines,
        string tableName,
        IReadOnlyDictionary<string, string>? values)
    {
        if (values is not { Count: > 0 })
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add($"[{tableName}]");
        foreach (var (key, value) in values)
        {
            lines.Add($"{TomlString(key)} = {TomlString(value)}");
        }
    }

    private void PublishSessionsChanged()
    {
        try { SessionsChanged?.Invoke(this, EventArgs.Empty); }
        catch { }
    }

    private void PublishWarning(string message)
    {
        Action<string>? handlers;
        lock (_warningSync)
        {
            handlers = _warningRaised;
            if (handlers is null)
            {
                _pendingWarnings.Add(message);
                return;
            }
        }

        try { handlers.Invoke(message); }
        catch { }
    }

    private string GetHomePath(CodexLaunchProfile profile)
    {
        // Preserve the original CLI home path for profiles saved before runtime support.
        var runtimeSuffix = profile.RuntimeKind == CodexLaunchRuntimeKind.Desktop ? "-desktop" : string.Empty;
        return Path.Combine(
            _paths.LaunchHomesDir,
            $"{profile.Id:N}-{(int)profile.TargetKind}-{profile.CredentialProfileId:N}{runtimeSuffix}");
    }

    private static string GetManifestPath(string home) => Path.Combine(home, ManifestFileName);

    private static string ResolveWorkingDirectory(string? configuredPath)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(configuredPath.Trim());
        if (!Directory.Exists(candidate))
        {
            throw new InvalidOperationException($"Working directory does not exist: {candidate}");
        }

        return candidate;
    }

    private static void ValidateProfile(CodexLaunchProfile profile)
    {
        if (profile.Id == Guid.Empty)
        {
            throw new InvalidOperationException("The launch profile needs a stable identifier.");
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new InvalidOperationException("Enter a name for the launch profile.");
        }

        if (profile.CredentialProfileId == Guid.Empty ||
            profile.TargetKind is not (CodexLaunchTargetKind.ChatGptAccount or CodexLaunchTargetKind.ApiProvider) ||
            profile.RuntimeKind is not (CodexLaunchRuntimeKind.Cli or CodexLaunchRuntimeKind.Desktop))
        {
            throw new InvalidOperationException("Choose a valid runtime and ChatGPT account or API provider for the launch profile.");
        }
    }

    private static List<string> ParseArguments(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        var argumentStarted = false;
        for (var i = 0; i < commandLine.Length;)
        {
            if (char.IsWhiteSpace(commandLine[i]) && !inQuotes)
            {
                if (argumentStarted)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    argumentStarted = false;
                }

                i++;
                continue;
            }

            var slashCount = 0;
            while (i < commandLine.Length && commandLine[i] == '\\')
            {
                slashCount++;
                i++;
            }

            if (i < commandLine.Length && commandLine[i] == '"')
            {
                current.Append('\\', slashCount / 2);
                argumentStarted = true;
                if (slashCount % 2 == 0)
                {
                    inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('"');
                }

                i++;
                continue;
            }

            current.Append('\\', slashCount);
            if (i < commandLine.Length)
            {
                current.Append(commandLine[i++]);
                argumentStarted = true;
            }
        }

        if (inQuotes)
        {
            throw new InvalidOperationException("Arguments contain an unmatched double quote.");
        }

        if (argumentStarted)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static void ValidateArguments(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument.Equals("-m", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--model", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--config", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-p", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--profile", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-C", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--cd", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--cd=", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--user-data-dir", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--user-data-dir=", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--api-key", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Argument '{argument}' can override the launch profile's account, model, CODEX_HOME, Electron data directory, or working directory and is not allowed here.");
            }
        }
    }

    private static DateTimeOffset? TryGetStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private static CodexLaunchSession ToSession(LaunchManifest manifest, DateTimeOffset? observedStartFallback = null) => new(
        manifest.SessionId,
        manifest.LaunchProfileId,
        manifest.TargetKind,
        manifest.RuntimeKind ?? CodexLaunchRuntimeKind.Cli,
        manifest.ProfileName,
        manifest.CredentialName,
        manifest.Model,
        manifest.WorkingDirectory,
        manifest.ProcessId,
        manifest.ProcessStartedAt != default
            ? manifest.ProcessStartedAt
            : observedStartFallback ?? manifest.ProcessStartedAt);

    private CodexLaunchRuntimeKind ResolveRuntimeKind(LaunchManifest manifest, string home)
    {
        // The directory suffix is the original runtime identity marker. It must
        // take precedence because older manifests serialized the enum's default
        // value (CLI) even for Desktop profiles.
        if (Path.GetFileName(home).EndsWith("-desktop", StringComparison.OrdinalIgnoreCase))
        {
            return CodexLaunchRuntimeKind.Desktop;
        }

        if (manifest.RuntimeKind is { } savedRuntimeKind &&
            Enum.IsDefined(savedRuntimeKind))
        {
            return savedRuntimeKind;
        }

        // For legacy CLI homes without a suffix, use the saved profile preference
        // when available, then preserve the historical CLI default.
        var configuredRuntimeKind = _settings.CodexLaunchProfiles?
            .FirstOrDefault(profile => profile.Id == manifest.LaunchProfileId)
            ?.RuntimeKind;
        return configuredRuntimeKind is { } fallbackRuntimeKind &&
               Enum.IsDefined(fallbackRuntimeKind)
            ? fallbackRuntimeKind
            : CodexLaunchRuntimeKind.Cli;
    }

    private sealed class SessionState(
        Process process,
        string home,
        LaunchManifest manifest,
        CodexLaunchSession presentation,
        IDisposable? profileLock)
    {
        public Process Process { get; } = process;
        public string Home { get; } = home;
        public LaunchManifest Manifest { get; } = manifest;
        public CodexLaunchSession Presentation { get; } = presentation;
        public IDisposable? ProfileLock { get; set; } = profileLock;
        public SemaphoreSlim CompletionGate { get; } = new(1, 1);
        public bool IsCompleted { get; set; }
        public bool WatcherAttached { get; set; }
    }

    private sealed class LaunchManifest
    {
        public Guid SessionId { get; set; }
        public Guid LaunchProfileId { get; set; }
        public CodexLaunchTargetKind TargetKind { get; set; }
        public CodexLaunchRuntimeKind? RuntimeKind { get; set; }
        public Guid CredentialProfileId { get; set; }
        public string ProfileName { get; set; } = string.Empty;
        public string CredentialName { get; set; } = string.Empty;
        public string? Model { get; set; }
        public string WorkingDirectory { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public DateTimeOffset ProcessStartedAt { get; set; }
        public bool? ProcessStartMayHaveBeenIssued { get; set; }
        public string? InitialAuthFingerprint { get; set; }
    }

    private sealed class SharedChatsManifest
    {
        public int Version { get; set; }
        public string CodexHome { get; set; } = string.Empty;
        public string SqliteHome { get; set; } = string.Empty;
        public bool OwnsSessionsLink { get; set; }
        public bool OwnsArchivedSessionsLink { get; set; }
        public bool OwnsSessionIndexLink { get; set; }
    }
}
