using System.Security.Cryptography;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Codex.Threads;

namespace CodexSwitcher.Infra.Codex.Runtime;

/// <summary>
/// Transactionally prepares the live Codex slot for a user-driven OpenAI sign-in.
/// Saved Switchboard profiles, vault blobs, and TOTP data are never deleted.
/// </summary>
public sealed class CodexSignedOutService : ICodexSignedOutService, IDisposable
{
    private static readonly TimeSpan DesktopCloseTimeout = TimeSpan.FromSeconds(12);

    private readonly ICodexRuntimeResolver _runtimeResolver;
    private readonly AppSettings _settings;
    private readonly IProcessManager _processes;
    private readonly ISwitchboardCodexProcessRegistry _processRegistry;
    private readonly AccountRegistry _accountRegistry;
    private readonly CodexAuthReconciler _authReconciler;
    private readonly ICodexRoutingConfigStore _routing;
    private readonly IFileSystem _fs;
    private readonly AppPaths _paths;
    private readonly IAuditLog _audit;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CodexSignedOutService(
        ICodexRuntimeResolver runtimeResolver,
        AppSettings settings,
        IProcessManager processes,
        ISwitchboardCodexProcessRegistry processRegistry,
        AccountRegistry accountRegistry,
        CodexAuthReconciler authReconciler,
        ICodexRoutingConfigStore routing,
        IFileSystem fs,
        AppPaths paths,
        IAuditLog audit)
    {
        _runtimeResolver = runtimeResolver ?? throw new ArgumentNullException(nameof(runtimeResolver));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _processRegistry = processRegistry ?? throw new ArgumentNullException(nameof(processRegistry));
        _accountRegistry = accountRegistry ?? throw new ArgumentNullException(nameof(accountRegistry));
        _authReconciler = authReconciler ?? throw new ArgumentNullException(nameof(authReconciler));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task<SignedOutOpenAiResult> OpenSignInAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        byte[]? authBackup = null;
        byte[]? configBackup = null;
        byte[]? routingStateBackup = null;
        byte[]? routingStateAfterMutation = null;
        string? configFingerprintAfterMutation = null;
        bool authExisted = false;
        bool configExisted = false;
        bool routingStateExisted = false;
        bool desktopWasRunning = false;
        bool desktopCloseCompleted = false;
        bool routingMutationStarted = false;
        string? originalConfigFingerprint = null;
        var stage = "preflight";

        try
        {
            if (_processes.AnyCodexCliRunning())
            {
                return Fail("Close the running Codex CLI before opening a signed-out Desktop session.");
            }

            var runtime = _runtimeResolver.ResolveCurrentRuntime(_settings.CodexExecutablePathOverride);
            if (string.IsNullOrWhiteSpace(runtime.ExecutablePath) || !_fs.FileExists(runtime.ExecutablePath))
            {
                return Fail("The installed Codex runtime could not be resolved; no sign-out changes were made.");
            }

            // First reconcile the live slot. If Codex refreshed a managed
            // credential, CodexAuthReconciler writes that exact auth.json into the
            // existing encrypted vault before the live slot is cleared.
            stage = "reconcile-profile";
            _authReconciler.Reconcile();
            _accountRegistry.Save();
            var savedProfileCount = _accountRegistry.Profiles.Count;
            var savedBlobFingerprints = _accountRegistry.Profiles
                .Select(profile => profile.BlobFingerprint)
                .ToArray();

            authExisted = _fs.FileExists(_paths.Codex.ActiveAuthPath);
            if (authExisted)
            {
                authBackup = _fs.ReadAllBytes(_paths.Codex.ActiveAuthPath);
            }

            configExisted = _fs.FileExists(_paths.Codex.ConfigTomlPath);
            if (configExisted)
            {
                configBackup = _fs.ReadAllBytes(_paths.Codex.ConfigTomlPath);
            }

            routingStateExisted = _fs.FileExists(_paths.RoutingStatePath);
            if (routingStateExisted)
            {
                routingStateBackup = _fs.ReadAllBytes(_paths.RoutingStatePath);
            }

            var originalRouting = _routing.ReadRoutingState(_paths.Codex.ConfigTomlPath);
            originalConfigFingerprint = originalRouting.Fingerprint;
            desktopWasRunning = _processes.FindRunningCodexProcesses()
                .Any(process => process.Kind == CodexProcessKind.DesktopApp);

            // Do not kill the desktop process if it cannot close normally:
            // the window may be displaying an unsaved-work confirmation.
            stage = "close-desktop";
            desktopCloseCompleted = await _processes.CloseDesktopGracefullyAsync(
                DesktopCloseTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!desktopCloseCompleted)
            {
                RestoreDesktopIfNeeded(desktopWasRunning);
                return Fail("Codex Desktop did not close gracefully. No sign-out or routing changes were made.");
            }

            if (_processes.AnyCodexCliRunning())
            {
                RestoreDesktopIfNeeded(desktopWasRunning);
                return Fail("A Codex CLI process started during preparation. No sign-out changes were made.");
            }

            if (!AuthSlotMatches(authExisted, authBackup))
            {
                RestoreDesktopIfNeeded(desktopWasRunning);
                return Fail("The live Codex session changed during preparation. No sign-out changes were made.");
            }

            stage = "remove-api-projection";
            routingMutationStarted = true;
            configFingerprintAfterMutation = _routing.ReturnToOpenAi(
                _paths.Codex.ConfigTomlPath,
                model: null,
                expectedFingerprint: originalRouting.Fingerprint);
            routingStateAfterMutation = _fs.FileExists(_paths.RoutingStatePath)
                ? _fs.ReadAllBytes(_paths.RoutingStatePath)
                : null;

            var openAiRouting = _routing.ReadRoutingState(_paths.Codex.ConfigTomlPath);
            if (!string.Equals(openAiRouting.ModelProvider, "openai", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(openAiRouting.ModelCatalogJson) ||
                openAiRouting.SwitchboardProviders.Count != 0)
            {
                throw new InvalidOperationException("The API routing projection could not be removed safely.");
            }

            stage = "official-account-logout";
            await LogoutAndVerifyAsync(runtime.ExecutablePath, cancellationToken).ConfigureAwait(false);

            stage = "reconcile-signed-out-state";
            _authReconciler.Reconcile();
            _accountRegistry.Save();
            if (_accountRegistry.Profiles.Count != savedProfileCount ||
                !_accountRegistry.Profiles.Select(profile => profile.BlobFingerprint)
                    .SequenceEqual(savedBlobFingerprints, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Saved profile metadata changed during sign-out.");
            }

            stage = "launch-desktop";
            if (!_processes.TryLaunchDesktop())
            {
                throw new InvalidOperationException("Codex Desktop could not be launched after sign-out.");
            }

            _audit.Record("open-codex-sign-in", "signed-out", "Official app-server logout verified; saved profiles retained.");
            return new SignedOutOpenAiResult(
                true,
                "Codex is signed out and has been opened. Sign in manually in Codex, then press Detect account in Switchboard.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CapturePartialRoutingMutation(
                routingMutationStarted,
                originalConfigFingerprint,
                ref configFingerprintAfterMutation,
                ref routingStateAfterMutation,
                routingStateBackup);
            var rollback = RestorePreviousState(
                authExisted,
                authBackup,
                configExisted,
                configBackup,
                routingStateExisted,
                routingStateBackup,
                routingStateAfterMutation,
                configFingerprintAfterMutation,
                routingMutationStarted,
                desktopWasRunning);
            _audit.Record("open-codex-sign-in", "canceled", $"stage={stage}; rollback={(rollback ? "ok" : "partial")}");
            return new SignedOutOpenAiResult(false, rollback
                ? "Sign-out was canceled; the previous Codex state was restored where unchanged."
                : "Sign-out was canceled; a concurrent change was left untouched for safety.");
        }
        catch (Exception ex)
        {
            CapturePartialRoutingMutation(
                routingMutationStarted,
                originalConfigFingerprint,
                ref configFingerprintAfterMutation,
                ref routingStateAfterMutation,
                routingStateBackup,
                ex is not ConcurrentModificationException);
            var rollback = RestorePreviousState(
                authExisted,
                authBackup,
                configExisted,
                configBackup,
                routingStateExisted,
                routingStateBackup,
                routingStateAfterMutation,
                configFingerprintAfterMutation,
                routingMutationStarted,
                desktopWasRunning,
                ex is not ConcurrentModificationException);
            _audit.Record("open-codex-sign-in", "failed", $"stage={stage}; error={ex.GetType().Name}; rollback={(rollback ? "ok" : "partial")}");
            return new SignedOutOpenAiResult(false, rollback
                ? "Codex could not be prepared for sign-in. The previous live session and routing were restored."
                : "Codex could not be prepared for sign-in. A concurrent change was left untouched for safety.");
        }
        finally
        {
            if (authBackup is not null)
            {
                CryptographicOperations.ZeroMemory(authBackup);
            }
            _gate.Release();
        }
    }

    private async Task LogoutAndVerifyAsync(string executablePath, CancellationToken cancellationToken)
    {
        await using var client = new CodexAppServerClient(
            executablePath,
            _paths.Codex.CodexHome,
            _processRegistry);
        await client.StartAsync(cancellationToken).ConfigureAwait(false);

        // The logout action is the official app-server API. Never truncate or
        // synthesize auth.json to create a signed-out state.
        Exception? logoutError = null;
        try
        {
            await client.RequestAsync(
                "account/logout",
                new { },
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Verify the postcondition below: some runtimes can finish clearing
            // auth.json before a notification/response failure reaches the client.
            logoutError = ex;
        }

        var accountRead = await client.RequestAsync(
            "account/read",
            new { refreshToken = false },
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);

        var stillAuthenticated = accountRead.TryGetProperty("account", out var account) &&
                                 account.ValueKind is not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined);
        if (_fs.FileExists(_paths.Codex.ActiveAuthPath) || stillAuthenticated)
        {
            throw new InvalidOperationException(logoutError is null
                ? "The installed Codex runtime still reports an authenticated account after account/logout."
                : "The installed Codex runtime did not verify account/logout.");
        }

        await client.StopAsync().ConfigureAwait(false);
    }

    private bool AuthSlotMatches(bool existed, byte[]? expected)
    {
        var existsNow = _fs.FileExists(_paths.Codex.ActiveAuthPath);
        if (existsNow != existed)
        {
            return false;
        }

        return !existed || expected is not null &&
            _fs.ReadAllBytes(_paths.Codex.ActiveAuthPath).AsSpan().SequenceEqual(expected);
    }

    private bool RestorePreviousState(
        bool authExisted,
        byte[]? authBackup,
        bool configExisted,
        byte[]? configBackup,
        bool routingStateExisted,
        byte[]? routingStateBackup,
        byte[]? routingStateAfterMutation,
        string? configFingerprintAfterMutation,
        bool routingMutationStarted,
        bool desktopWasRunning,
        bool mayRestoreConfig = true)
    {
        var complete = true;
        try
        {
            if (authExisted && authBackup is not null)
            {
                if (!_fs.FileExists(_paths.Codex.ActiveAuthPath))
                {
                    _fs.WriteAllBytesAtomic(_paths.Codex.ActiveAuthPath, authBackup);
                }
                else if (!_fs.ReadAllBytes(_paths.Codex.ActiveAuthPath).AsSpan().SequenceEqual(authBackup))
                {
                    // Do not overwrite a newer login that appeared concurrently.
                    complete = false;
                }
            }
            else if (!authExisted && _fs.FileExists(_paths.Codex.ActiveAuthPath))
            {
                // A newly appeared login is not ours to delete during rollback.
                complete = false;
            }
        }
        catch
        {
            complete = false;
        }

        if (routingMutationStarted && mayRestoreConfig)
        {
            try
            {
                var current = _routing.ReadRoutingState(_paths.Codex.ConfigTomlPath);
                var isStillOurConfig = configFingerprintAfterMutation is null ||
                    string.Equals(current.Fingerprint, configFingerprintAfterMutation, StringComparison.Ordinal);
                if (isStillOurConfig)
                {
                    if (configExisted && configBackup is not null)
                    {
                        _routing.RestoreExactBytes(_paths.Codex.ConfigTomlPath, configBackup);
                    }
                    else if (_fs.FileExists(_paths.Codex.ConfigTomlPath))
                    {
                        _fs.Delete(_paths.Codex.ConfigTomlPath);
                    }
                }
                else
                {
                    complete = false;
                }
            }
            catch
            {
                complete = false;
            }

            try
            {
                var stateExistsNow = _fs.FileExists(_paths.RoutingStatePath);
                var currentState = stateExistsNow ? _fs.ReadAllBytes(_paths.RoutingStatePath) : null;
                var isStillOurRoutingState = routingStateAfterMutation is null
                    ? !routingStateExisted || !stateExistsNow
                    : currentState is not null && currentState.AsSpan().SequenceEqual(routingStateAfterMutation);
                if (isStillOurRoutingState)
                {
                    if (routingStateExisted && routingStateBackup is not null)
                    {
                        _fs.WriteAllBytesAtomic(_paths.RoutingStatePath, routingStateBackup);
                    }
                    else if (stateExistsNow)
                    {
                        _fs.Delete(_paths.RoutingStatePath);
                    }
                }
                else
                {
                    complete = false;
                }
            }
            catch
            {
                complete = false;
            }
        }

        try
        {
            _authReconciler.Reconcile();
            _accountRegistry.Save();
        }
        catch
        {
            complete = false;
        }

        try
        {
            if (desktopWasRunning)
            {
                _processes.TryLaunchDesktop();
            }
        }
        catch
        {
            complete = false;
        }

        return complete;
    }

    private void CapturePartialRoutingMutation(
        bool mutationStarted,
        string? originalConfigFingerprint,
        ref string? currentConfigFingerprint,
        ref byte[]? routingStateAfterMutation,
        byte[]? originalRoutingState,
        bool mayCapture = true)
    {
        if (!mutationStarted || !mayCapture)
        {
            return;
        }

        try
        {
            var current = _routing.ReadRoutingState(_paths.Codex.ConfigTomlPath);
            if (currentConfigFingerprint is null &&
                !string.Equals(current.Fingerprint, originalConfigFingerprint, StringComparison.Ordinal))
            {
                currentConfigFingerprint = current.Fingerprint;
            }

            if (routingStateAfterMutation is null)
            {
                var exists = _fs.FileExists(_paths.RoutingStatePath);
                var bytes = exists ? _fs.ReadAllBytes(_paths.RoutingStatePath) : null;
                if (!NullableByteArraysEqual(bytes, originalRoutingState))
                {
                    routingStateAfterMutation = bytes;
                }
            }
        }
        catch
        {
            // Rollback below will report partial if it cannot prove ownership.
        }
    }

    private static bool NullableByteArraysEqual(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private void RestoreDesktopIfNeeded(bool wasRunning)
    {
        if (wasRunning)
        {
            _processes.TryLaunchDesktop();
        }
    }

    private SignedOutOpenAiResult Fail(string message)
    {
        _audit.Record("open-codex-sign-in", "not-started", "Precondition failed; no auth or routing mutation was attempted.");
        return new SignedOutOpenAiResult(false, message);
    }

    public void Dispose() => _gate.Dispose();
}
