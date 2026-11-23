using System.Diagnostics;
using System.Text.Json;
using Codex.RuntimeKit.Desktop.DebugTransport;
using Codex.RuntimeKit.Desktop.Discovery;
using Codex.RuntimeKit.Desktop.Extensions;
using Codex.RuntimeKit.Desktop.Renderer;
using CodexSwitcher.App.Localization;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Settings.Models;

namespace CodexSwitcher.App.Features.CodexDesktopBalance;

/// <summary>Owns the verified renderer connection and the reversible UI for one managed session.</summary>
internal sealed class CodexDesktopBalanceRendererAttachment(
    CodexDesktopDebugEndpoint endpoint,
    CodexDesktopCdpSession renderer,
    CodexDesktopInstallation installation,
    bool preferNative) : IAsyncDisposable
{
    private const string RootAttribute = "data-codex-control-center-api-balance";
    private static readonly CodexDesktopDebugTransport Transport = new();
    private static readonly WindowsCodexDesktopListenerOwnerResolver ListenerOwner = new();
    private readonly CodexDesktopExtensionHost _host = new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [CodexDesktopBalanceRendererExtension.Id] = CodexDesktopBalanceRendererExtension.ApprovedSha256,
        [CodexDesktopNativeUsagePackage.Id] = CodexDesktopNativeUsagePackage.ApprovedSha256,
    });
    private string? _extensionId;
    private bool _installed;
    private bool _nativeRejected;

    public bool IsNativeActive => _installed && _extensionId == CodexDesktopNativeUsagePackage.Id;

    public bool Matches(CodexDesktopDebugEndpoint activeEndpoint, CodexDesktopRendererTarget activeTarget, bool currentPreference) =>
        endpoint == activeEndpoint && renderer.Target == activeTarget && preferNative == currentPreference;

    public static bool TryResolveEndpoint(CodexLaunchSession session, CodexDesktopInstallation installation, string codeHome,
        out CodexDesktopDebugEndpoint? endpoint)
    {
        endpoint = null;
        if (!CodexDesktopDebugEndpoint.TryReadManagedProfile(Path.Combine(codeHome, "user-data"), installation.ExecutablePath,
                ListenerOwner, out var candidate, out _) || candidate?.ProcessId != session.ProcessId)
        {
            return false;
        }

        using var process = Process.GetProcessById(session.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != session.StartedAt.UtcDateTime ||
            process.MainWindowHandle == IntPtr.Zero)
        {
            return false;
        }
        endpoint = candidate;
        return true;
    }

    public static Task<IReadOnlyList<CodexDesktopRendererTarget>> GetTargetsAsync(
        CodexDesktopDebugEndpoint endpoint, CancellationToken cancellationToken) =>
        Transport.GetTargetsAsync(endpoint, cancellationToken);

    public static CodexDesktopRendererSelection SelectRenderer(
        CodexDesktopDebugEndpoint endpoint, IReadOnlyList<CodexDesktopRendererTarget> targets) =>
        CodexDesktopRendererVerifier.SelectMainRenderer(targets, endpoint.Port);

    public static async Task<CodexDesktopBalanceRendererAttachment?> ConnectAsync(CodexDesktopDebugEndpoint endpoint,
        IReadOnlyList<CodexDesktopRendererTarget> targets, CodexDesktopInstallation installation, bool preferNative,
        CancellationToken cancellationToken)
    {
        var attach = await Transport.AttachToMainRendererAsync(endpoint, targets, cancellationToken).ConfigureAwait(false);
        return attach.Session is { } verified ? new(endpoint, verified, installation, preferNative) : null;
    }

    public async Task<string?> PresentAsync(Func<CodexDesktopBalancePresentation?> currentPresentation, bool russian,
        CancellationToken cancellationToken)
    {
        var native = await renderer.ProbeUniqueVisibleAnchorAsync(CodexDesktopBalanceAnchorPolicy.NativeUsageLabelSelector,
            cancellationToken).ConfigureAwait(false);
        if (preferNative && native.IsUniqueVisible)
            return Strings.Current.CodexDesktopBalanceStatusNoApiRoute;

        if (preferNative && !_nativeRejected && CodexDesktopNativeUsagePackage.SupportsInstallation(installation) &&
            _extensionId != CodexDesktopBalanceRendererExtension.Id)
        {
            var rail = await renderer.ProbeUniqueVisibleAnchorAsync(CodexDesktopBalanceAnchorPolicy.NavigationRailSelector,
                cancellationToken).ConfigureAwait(false);
            if (!rail.IsUniqueVisible && !(await renderer.ProbeUniqueVisibleAnchorAsync(
                    CodexDesktopBalanceAnchorPolicy.SidebarContentSelector, cancellationToken).ConfigureAwait(false)).IsUniqueVisible)
            {
                return Strings.Current.CodexDesktopBalanceStatusAnchorUnverified;
            }

            var alreadyNative = IsNativeActive;
            var failure = await PresentNativeAsync(currentPresentation, russian, cancellationToken).ConfigureAwait(false);
            if (failure is null || alreadyNative || failure == Strings.Current.CodexDesktopBalanceStatusNoPresentation)
                return failure;

            _nativeRejected = true;
        }

        if (_extensionId == CodexDesktopNativeUsagePackage.Id &&
            !await RemoveExtensionAsync(cancellationToken).ConfigureAwait(false))
        {
            return Strings.Current.CodexDesktopBalanceStatusInstallFailed;
        }

        var toolbar = !native.IsUniqueVisible
            ? await renderer.ProbeUniqueVisibleAnchorAsync(CodexDesktopBalanceAnchorPolicy.HeaderToolbarSelector,
                cancellationToken).ConfigureAwait(false)
            : null;
        var anchor = CodexDesktopBalanceAnchorPolicy.SelectAnchor(native, toolbar);
        return anchor is null
            ? Strings.Current.CodexDesktopBalanceStatusAnchorUnverified
            : await PresentFallbackAsync(anchor, currentPresentation, russian, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> PresentNativeAsync(Func<CodexDesktopBalancePresentation?> currentPresentation, bool russian,
        CancellationToken cancellationToken)
    {
        var needsInstall = !_installed;
        if (needsInstall)
        {
            var package = await CodexDesktopNativeUsagePackage.TryLoadAsync(installation, cancellationToken).ConfigureAwait(false);
            if (package is null || !await InstallAsync(package, cancellationToken).ConfigureAwait(false))
                return Strings.Current.CodexDesktopBalanceStatusInstallFailed;
        }

        var presentation = currentPresentation();
        var payload = presentation is null ? null : CodexDesktopNativeUsagePayload.FromPresentation(presentation, russian);
        if (payload is null) return Strings.Current.CodexDesktopBalanceStatusNoPresentation;

        if (!await SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false))
            return Strings.Current.CodexDesktopBalanceStatusInstallFailed;

        // Loading the original asynchronous usage chunk and committing its React root can take a turn.
        for (var attempt = 0; attempt < (needsInstall ? 12 : 3); attempt++)
        {
            // The bundled renderer sets this marker after measuring both arcs above the profile.
            var ready = await renderer.ProbeUniqueVisibleAnchorAsync($"[{CodexDesktopNativeUsagePackage.RootAttribute}]",
                cancellationToken).ConfigureAwait(false);
            if (ready.IsUniqueVisible)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
        }

        return Strings.Current.CodexDesktopBalanceStatusPlacementFailed;
    }

    private async Task<string?> PresentFallbackAsync(string anchor, Func<CodexDesktopBalancePresentation?> currentPresentation,
        bool russian, CancellationToken cancellationToken)
    {
        if (!_installed)
        {
            var script = CodexDesktopBalanceRendererExtension.LoadJavaScript();
            if (!CodexDesktopBalanceRendererExtension.MatchesApprovedHash(script))
                return Strings.Current.CodexDesktopBalanceStatusAssetRejected;
            var package = new CodexDesktopExtensionPackage(
                CodexDesktopBalanceRendererExtension.Id,
                Version.Parse(CodexDesktopBalanceRendererExtension.PackageVersion),
                CodexDesktopBalanceCompatibilityPolicy.MinimumSupportedVersion,
                CodexDesktopBalanceCompatibilityPolicy.MaximumSupportedVersion,
                CodexDesktopBalanceRendererExtension.ApprovedSha256,
                script)
            {
                RestorePayloadOnReload = false,
            };
            if (!await InstallAsync(package, cancellationToken).ConfigureAwait(false))
                return Strings.Current.CodexDesktopBalanceStatusInstallFailed;
        }

        var presentation = currentPresentation();
        var payload = presentation is null ? null : CodexDesktopBalanceRendererPayload.FromPresentation(presentation, russian);
        if (payload is null) return Strings.Current.CodexDesktopBalanceStatusNoPresentation;

        if (!await SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false))
            return Strings.Current.CodexDesktopBalanceStatusInstallFailed;

        return await renderer.IsExtensionAdjacentToAnchorAsync(anchor, RootAttribute, cancellationToken).ConfigureAwait(false)
            ? null
            : Strings.Current.CodexDesktopBalanceStatusPlacementFailed;
    }

    private async Task<bool> InstallAsync(CodexDesktopExtensionPackage package, CancellationToken cancellationToken)
    {
        _extensionId = package.Id;
        var result = await _host.InstallOrUpdateAsync(package, installation.Version, renderer, cancellationToken).ConfigureAwait(false);
        _installed = result.Succeeded;
        return _installed;
    }

    private async Task<bool> SendPayloadAsync<TPayload>(TPayload payload, CancellationToken cancellationToken)
    {
        var result = await _host.UpdatePayloadAsync(_extensionId!, JsonSerializer.SerializeToElement(payload),
            renderer, cancellationToken).ConfigureAwait(false);
        return result.State == CodexDesktopExtensionResultState.Updated;
    }

    private async Task<bool> RemoveExtensionAsync(CancellationToken cancellationToken)
    {
        if (_extensionId is not { } id) return true;
        var result = await _host.RemoveAsync(id, renderer, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) return false;

        _extensionId = null;
        _installed = false;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_extensionId is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await RemoveExtensionAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A disconnected renderer drops its live presentation when its lease expires.
        }
        finally
        {
            await renderer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
