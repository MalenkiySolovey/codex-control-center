using System.Text.Json;
using Codex.RuntimeKit.Desktop.Compatibility;
using Codex.RuntimeKit.Desktop.DebugTransport;
using Codex.RuntimeKit.Desktop.Discovery;
using Codex.RuntimeKit.Desktop.Extensions;
using Codex.RuntimeKit.Desktop.Renderer;
using CodexSwitcher.App.Localization;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Infra.Common.Paths;
using Microsoft.Extensions.Hosting;

namespace CodexSwitcher.App.Features.CodexDesktopBalance;

/// <summary>
/// Owns one reversible balance extension per verified, Control Center managed API Desktop
/// session. Provider and credential identity are resolved from that session, never from one
/// global launch-profile selection.
/// </summary>
public sealed class CodexDesktopBalanceController : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BillingRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly Version SupportedDesktopVersion = new(26, 1117, 3838, 0);
    private const string AnchorSelector = "[data-ambient-usage-label]";
    private const string ExtensionRootAttribute = "data-codex-control-center-api-balance";

    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly ICodexLaunchService _launchService;
    private readonly IApiProviderStore _providerStore;
    private readonly IApiKeySecretStore _apiKeyStore;
    private readonly IProviderInspectionService _inspectionService;
    private readonly ICodexRoutingConfigStore _routingConfigStore;
    private readonly CodexDesktopDiscovery _discovery = new();
    private readonly CodexDesktopDebugTransport _transport = new();
    private readonly WindowsCodexDesktopListenerOwnerResolver _ownerResolver = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Dictionary<Guid, DateTimeOffset> _lastBillingRefresh = [];
    private readonly Dictionary<Guid, SessionAttachment> _attachments = [];
    private string _statusText = Strings.Current.CodexDesktopBalanceStatusOff;

    public CodexDesktopBalanceController(
        AppSettings settings,
        AppPaths paths,
        ICodexLaunchService launchService,
        IApiProviderStore providerStore,
        IApiKeySecretStore apiKeyStore,
        IProviderInspectionService inspectionService,
        ICodexRoutingConfigStore routingConfigStore)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _providerStore = providerStore ?? throw new ArgumentNullException(nameof(providerStore));
        _apiKeyStore = apiKeyStore ?? throw new ArgumentNullException(nameof(apiKeyStore));
        _inspectionService = inspectionService ?? throw new ArgumentNullException(nameof(inspectionService));
        _routingConfigStore = routingConfigStore ?? throw new ArgumentNullException(nameof(routingConfigStore));
    }

    public event Action? StatusChanged;

    public string StatusText => _statusText;

    public void NotifyConfigurationChanged()
    {
        if (!_settings.CodexDesktopApiBalanceIntegrationEnabled)
        {
            SetStatus(Strings.Current.CodexDesktopBalanceStatusOff);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await DetachAllAsync(CancellationToken.None).ConfigureAwait(false);
                SetStatus(Strings.Current.CodexDesktopBalanceStatusUnavailable);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cleanup.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await _refreshLock.WaitAsync(cleanup.Token).ConfigureAwait(false);
            try { await DetachAllAsync(cleanup.Token).ConfigureAwait(false); }
            finally { _refreshLock.Release(); }
        }
        catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
        {
            // A stopped or reloading renderer owns no process state; only owned extensions are removed.
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_settings.CodexDesktopApiBalanceIntegrationEnabled)
            {
                await DetachAllAsync(cancellationToken).ConfigureAwait(false);
                SetStatus(Strings.Current.CodexDesktopBalanceStatusOff);
                return;
            }

            var runningApiSessions = _launchService.GetRunningSessions()
                .Where(session => session.TargetKind == CodexLaunchTargetKind.ApiProvider &&
                                  session.RuntimeKind == CodexLaunchRuntimeKind.Desktop)
                .ToArray();
            if (runningApiSessions.Length == 0)
            {
                await DetachAllAsync(cancellationToken).ConfigureAwait(false);
                SetStatus(Strings.Current.CodexDesktopBalanceStatusNoApiSessions);
                return;
            }

            var liveSessionIds = runningApiSessions.Select(session => session.Id).ToHashSet();
            foreach (var staleSessionId in _attachments.Keys.Where(id => !liveSessionIds.Contains(id)).ToArray())
            {
                await DetachSessionAsync(staleSessionId, cancellationToken).ConfigureAwait(false);
            }

            var ambiguousProfileIds = runningApiSessions
                .GroupBy(session => session.LaunchProfileId)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet();
            var launchProfiles = _settings.CodexLaunchProfiles ?? [];
            var providerProfiles = _providerStore.GetAll();
            var installation = _discovery.Discover();
            var outcomes = new List<SessionOutcome>(runningApiSessions.Length);

            foreach (var session in runningApiSessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ambiguousProfileIds.Contains(session.LaunchProfileId))
                {
                    await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                    outcomes.Add(new(false, Strings.Current.CodexDesktopBalanceStatusAmbiguousProfile));
                    continue;
                }

                if (!CodexDesktopBalanceSessionResolver.TryResolve(
                        session,
                        launchProfiles,
                        providerProfiles,
                        out var binding,
                        out var resolutionFailure) || binding is null)
                {
                    await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                    outcomes.Add(new(false, ResolutionStatus(resolutionFailure)));
                    continue;
                }

                var status = await ProcessSessionAsync(binding, installation, cancellationToken).ConfigureAwait(false);
                outcomes.Add(new(status == Strings.Current.CodexDesktopBalanceStatusInstalled, status));
            }

            var installedCount = outcomes.Count(outcome => outcome.IsInstalled);
            if (installedCount > 0)
            {
                SetStatus(Strings.Current.CodexDesktopBalanceStatusWatching(installedCount, outcomes.Count - installedCount));
            }
            else
            {
                SetStatus(outcomes[0].Status);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<string> ProcessSessionAsync(
        CodexDesktopBalanceSessionBinding binding,
        CodexDesktopDiscoveryResult discovery,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = binding.Session;
            var launchProfile = binding.LaunchProfile;
            var provider = binding.ProviderProfile;
            if (provider.Status != ApiProviderProfileStatus.Active)
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusProviderUnavailable;
            }

            var credentialOwnerId = provider.EndpointId ?? provider.Id;
            var credentialRevision = _apiKeyStore.GetApiKeyRevision(credentialOwnerId);
            if (!_apiKeyStore.HasApiKey(credentialOwnerId) || string.IsNullOrWhiteSpace(credentialRevision))
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusCredentialUnavailable;
            }

            if (session.CredentialOwnerId != credentialOwnerId ||
                !string.Equals(session.CredentialRevision, credentialRevision, StringComparison.Ordinal))
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusCredentialChanged;
            }

            var codeHome = GetCodeHomePath(launchProfile);
            if (!IsActiveApiRoute(codeHome, provider))
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusRouteChanged;
            }

            if (await RefreshBillingIfNeededAsync(provider, cancellationToken).ConfigureAwait(false) is { } inspectedProvider)
            {
                provider = inspectedProvider;
            }

            if (provider.Id != session.CredentialProfileId || !IsActiveApiRoute(codeHome, provider))
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusRouteChanged;
            }

            var currentCredentialRevision = _apiKeyStore.GetApiKeyRevision(credentialOwnerId);
            if (!string.Equals(currentCredentialRevision, credentialRevision, StringComparison.Ordinal))
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusCredentialChanged;
            }

            if (!discovery.IsAvailable || discovery.Installation is null || !_discovery.IsVerifiedInstallation(discovery.Installation))
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusDesktopUnavailable;
            }

            if (discovery.Installation.Version != SupportedDesktopVersion)
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusUnsupportedVersion;
            }

            var userData = Path.Combine(codeHome, "user-data");
            if (!CodexDesktopDebugEndpoint.TryReadManagedProfile(
                    userData,
                    discovery.Installation.ExecutablePath,
                    _ownerResolver,
                    out var endpoint,
                    out _) || endpoint is null || endpoint.ProcessId != session.ProcessId)
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusRestartProfile;
            }

            var targets = await _transport.GetTargetsAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var selection = CodexDesktopRendererVerifier.SelectMainRenderer(targets, endpoint.Port);
            if (selection.State != CodexDesktopRendererSelectionState.Selected || selection.Target is null)
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusWaitingForRenderer;
            }

            if (!_attachments.TryGetValue(session.Id, out var attachment) ||
                !attachment.Matches(binding, session.ProcessId, endpoint.Port, selection.Target.Id))
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                var attached = await _transport.AttachToMainRendererAsync(endpoint, targets, cancellationToken).ConfigureAwait(false);
                if (attached.State != CodexDesktopRendererSelectionState.Selected || attached.Session is null)
                {
                    return Strings.Current.CodexDesktopBalanceStatusWaitingForRenderer;
                }

                attachment = new SessionAttachment(
                    binding,
                    session.ProcessId,
                    endpoint.Port,
                    attached.Session.Target.Id,
                    attached.Session,
                    new CodexDesktopExtensionHost(new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [CodexDesktopBalanceRendererExtension.Id] = CodexDesktopBalanceRendererExtension.ApprovedSha256,
                    }));
                _attachments[session.Id] = attachment;
            }

            var anchor = await attachment.Renderer.ProbeUniqueVisibleAnchorAsync(AnchorSelector, cancellationToken).ConfigureAwait(false);
            var liveCompatibility = new CodexDesktopCompatibilityMatrix(
            [
                new CodexDesktopCompatibilityRule(
                    SupportedDesktopVersion,
                    SupportedDesktopVersion,
                    "codex-profile-footer-ambient-usage-label-v1",
                    RendererContractVerified: anchor.IsUniqueVisible),
            ]).Evaluate(discovery.Installation.Version);
            if (liveCompatibility.State != CodexDesktopCompatibilityState.Supported)
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusAnchorUnverified;
            }

            var codeHomeFingerprint = CodexDesktopBalancePresentationMapper.CreateCodeHomeFingerprint(codeHome);
            var presentation = CodexDesktopBalancePresentationMapper.Create(
                new ActiveTarget.Api(provider),
                codeHomeFingerprint,
                credentialRevision,
                DateTimeOffset.UtcNow);
            var payload = CodexDesktopBalanceRendererPayload.FromPresentation(
                presentation,
                System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase));
            if (payload is null)
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusNoPresentation;
            }

            var payloadJson = JsonSerializer.Serialize(payload);
            if (!attachment.IsInstalled)
            {
                var script = CodexDesktopBalanceRendererExtension.LoadJavaScript();
                if (!CodexDesktopBalanceRendererExtension.MatchesApprovedHash(script))
                {
                    await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                    return Strings.Current.CodexDesktopBalanceStatusAssetRejected;
                }

                var package = new CodexDesktopExtensionPackage(
                    CodexDesktopBalanceRendererExtension.Id,
                    new Version(0, 1, 0),
                    SupportedDesktopVersion,
                    SupportedDesktopVersion,
                    CodexDesktopBalanceRendererExtension.ApprovedSha256,
                    script);
                var installed = await attachment.ExtensionHost.InstallOrUpdateAsync(
                    package,
                    discovery.Installation.Version,
                    attachment.Renderer,
                    cancellationToken).ConfigureAwait(false);
                if (!installed.Succeeded)
                {
                    await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                    return Strings.Current.CodexDesktopBalanceStatusInstallFailed;
                }

                attachment.IsInstalled = true;
            }

            if (!string.Equals(payloadJson, attachment.LastPayloadJson, StringComparison.Ordinal))
            {
                using var payloadDocument = JsonDocument.Parse(payloadJson);
                var updated = await attachment.ExtensionHost.UpdatePayloadAsync(
                    CodexDesktopBalanceRendererExtension.Id,
                    payloadDocument.RootElement,
                    attachment.Renderer,
                    cancellationToken).ConfigureAwait(false);
                if (!updated.Succeeded)
                {
                    await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                    return Strings.Current.CodexDesktopBalanceStatusInstallFailed;
                }

                attachment.LastPayloadJson = payloadJson;
            }

            if (!await attachment.Renderer.IsExtensionAdjacentToAnchorAsync(
                    AnchorSelector,
                    ExtensionRootAttribute,
                    cancellationToken).ConfigureAwait(false))
            {
                await DetachSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                return Strings.Current.CodexDesktopBalanceStatusPlacementFailed;
            }

            return Strings.Current.CodexDesktopBalanceStatusInstalled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await DetachSessionAsync(binding.Session.Id, CancellationToken.None).ConfigureAwait(false);
            return Strings.Current.CodexDesktopBalanceStatusUnavailable;
        }
    }

    private async Task<ApiProviderProfile?> RefreshBillingIfNeededAsync(
        ApiProviderProfile profile,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lastAttempt = _lastBillingRefresh.GetValueOrDefault(profile.Id);
        var snapshotIsRecent = profile.LastBillingSnapshot is { } snapshot &&
                               now - snapshot.LastAttemptAt < BillingRefreshInterval;
        if (snapshotIsRecent || now - lastAttempt < BillingRefreshInterval)
        {
            return null;
        }

        _lastBillingRefresh[profile.Id] = now;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await _inspectionService.InspectAsync(profile.Id, timeout.Token).ConfigureAwait(false);
            return _providerStore.GetById(profile.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return _providerStore.GetById(profile.Id);
        }
        catch
        {
            return _providerStore.GetById(profile.Id);
        }
    }

    private bool IsActiveApiRoute(string codeHome, ApiProviderProfile profile)
    {
        var configPath = Path.Combine(codeHome, "config.toml");
        if (!File.Exists(configPath))
        {
            return false;
        }

        var routing = _routingConfigStore.ReadRoutingState(configPath);
        return string.Equals(routing.ModelProvider, profile.StableCodexProviderId, StringComparison.OrdinalIgnoreCase) &&
               routing.SwitchboardProviders.TryGetValue(profile.StableCodexProviderId, out var providerBlock) &&
               string.Equals(NormalizeBaseUrl(providerBlock.BaseUrl), NormalizeBaseUrl(profile.BaseUrl), StringComparison.OrdinalIgnoreCase);
    }

    private string GetCodeHomePath(CodexLaunchProfile profile) => Path.Combine(
        _paths.LaunchHomesDir,
        $"{profile.Id:N}-{(int)profile.TargetKind}-{profile.CredentialProfileId:N}-desktop");

    private async Task DetachAllAsync(CancellationToken cancellationToken)
    {
        foreach (var sessionId in _attachments.Keys.ToArray())
        {
            await DetachSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DetachSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_attachments.Remove(sessionId, out var attachment))
        {
            return;
        }

        if (attachment.IsInstalled)
        {
            try
            {
                await attachment.ExtensionHost.RemoveAsync(
                    CodexDesktopBalanceRendererExtension.Id,
                    attachment.Renderer,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The verified renderer may have navigated or exited; remove only owned nodes.
            }
        }

        try { await attachment.Renderer.DisposeAsync().ConfigureAwait(false); }
        catch { }
    }

    private void SetStatus(string value)
    {
        if (string.Equals(_statusText, value, StringComparison.Ordinal))
        {
            return;
        }

        _statusText = value;
        try { StatusChanged?.Invoke(); }
        catch { }
    }

    private static string ResolutionStatus(CodexDesktopBalanceSessionResolutionFailure failure) => failure switch
    {
        CodexDesktopBalanceSessionResolutionFailure.LaunchProfileMissing or
            CodexDesktopBalanceSessionResolutionFailure.AmbiguousLaunchProfile or
            CodexDesktopBalanceSessionResolutionFailure.LaunchProfileChanged => Strings.Current.CodexDesktopBalanceStatusInvalidProfile,
        CodexDesktopBalanceSessionResolutionFailure.CredentialProfileChanged => Strings.Current.CodexDesktopBalanceStatusCredentialChanged,
        CodexDesktopBalanceSessionResolutionFailure.ProviderProfileMissing or
            CodexDesktopBalanceSessionResolutionFailure.AmbiguousProviderProfile => Strings.Current.CodexDesktopBalanceStatusProviderUnavailable,
        _ => Strings.Current.CodexDesktopBalanceStatusNoPresentation,
    };

    private static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + uri.AbsolutePath.TrimEnd('/');
    }

    private readonly record struct SessionOutcome(bool IsInstalled, string Status);

    private sealed class SessionAttachment(
        CodexDesktopBalanceSessionBinding binding,
        int processId,
        int debugPort,
        string targetId,
        CodexDesktopCdpSession renderer,
        CodexDesktopExtensionHost extensionHost)
    {
        public Guid LaunchProfileId { get; } = binding.LaunchProfile.Id;
        public Guid ProviderProfileId { get; } = binding.ProviderProfile.Id;
        public Guid? CredentialOwnerId { get; } = binding.Session.CredentialOwnerId;
        public string? CredentialRevision { get; } = binding.Session.CredentialRevision;
        public int ProcessId { get; } = processId;
        public int DebugPort { get; } = debugPort;
        public string TargetId { get; } = targetId;
        public CodexDesktopCdpSession Renderer { get; } = renderer;
        public CodexDesktopExtensionHost ExtensionHost { get; } = extensionHost;
        public string? LastPayloadJson { get; set; }
        public bool IsInstalled { get; set; }

        public bool Matches(CodexDesktopBalanceSessionBinding current, int currentProcessId, int currentPort, string currentTargetId) =>
            LaunchProfileId == current.LaunchProfile.Id &&
            ProviderProfileId == current.ProviderProfile.Id &&
            CredentialOwnerId == current.Session.CredentialOwnerId &&
            string.Equals(CredentialRevision, current.Session.CredentialRevision, StringComparison.Ordinal) &&
            ProcessId == currentProcessId &&
            DebugPort == currentPort &&
            string.Equals(TargetId, currentTargetId, StringComparison.Ordinal);
    }
}
