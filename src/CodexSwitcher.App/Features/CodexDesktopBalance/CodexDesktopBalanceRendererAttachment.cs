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
    Version desktopVersion) : IAsyncDisposable
{
    private const string RootAttribute = "data-codex-control-center-api-balance";
    private static readonly CodexDesktopDebugTransport Transport = new();
    private static readonly WindowsCodexDesktopListenerOwnerResolver ListenerOwner = new();
    private readonly CodexDesktopExtensionHost _host = new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [CodexDesktopBalanceRendererExtension.Id] = CodexDesktopBalanceRendererExtension.ApprovedSha256,
    });
    private bool _installAttempted;
    private bool _installed;

    public bool Matches(CodexDesktopDebugEndpoint activeEndpoint, CodexDesktopRendererTarget activeTarget) =>
        endpoint == activeEndpoint && renderer.Target == activeTarget;

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
        if (process.HasExited || process.StartTime.ToUniversalTime() != session.StartedAt.UtcDateTime) return false;
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
        IReadOnlyList<CodexDesktopRendererTarget> targets, Version version, CancellationToken cancellationToken)
    {
        var attach = await Transport.AttachToMainRendererAsync(endpoint, targets, cancellationToken).ConfigureAwait(false);
        return attach.Session is { } verified ? new(endpoint, verified, version) : null;
    }

    public async Task<string?> PresentAsync(Func<CodexDesktopBalanceRendererPayload?> currentPayload, CancellationToken cancellationToken)
    {
        var native = await renderer.ProbeUniqueVisibleAnchorAsync(CodexDesktopBalanceAnchorPolicy.NativeUsageLabelSelector,
            cancellationToken).ConfigureAwait(false);
        var toolbar = !native.IsUniqueVisible
            ? await renderer.ProbeUniqueVisibleAnchorAsync(CodexDesktopBalanceAnchorPolicy.HeaderToolbarSelector,
                cancellationToken).ConfigureAwait(false)
            : null;
        var anchor = CodexDesktopBalanceAnchorPolicy.SelectAnchor(native, toolbar);
        if (anchor is null) return Strings.Current.CodexDesktopBalanceStatusAnchorUnverified;

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
            _installAttempted = true;
            var result = await _host.InstallOrUpdateAsync(package, desktopVersion, renderer, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded) return Strings.Current.CodexDesktopBalanceStatusInstallFailed;
            _installed = true;
        }

        var payload = currentPayload();
        if (payload is null) return Strings.Current.CodexDesktopBalanceStatusNoPresentation;

        var update = await _host.UpdatePayloadAsync(CodexDesktopBalanceRendererExtension.Id,
            JsonSerializer.SerializeToElement(payload), renderer, cancellationToken).ConfigureAwait(false);
        if (update.State != CodexDesktopExtensionResultState.Updated)
            return Strings.Current.CodexDesktopBalanceStatusInstallFailed;

        return await renderer.IsExtensionAdjacentToAnchorAsync(anchor, RootAttribute, cancellationToken).ConfigureAwait(false)
            ? null
            : Strings.Current.CodexDesktopBalanceStatusPlacementFailed;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_installAttempted)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _host.RemoveAsync(CodexDesktopBalanceRendererExtension.Id, renderer, timeout.Token).ConfigureAwait(false);
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
