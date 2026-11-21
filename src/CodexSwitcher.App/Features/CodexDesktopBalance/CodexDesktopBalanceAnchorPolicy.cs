using Codex.RuntimeKit.Desktop.DebugTransport;

namespace CodexSwitcher.App.Features.CodexDesktopBalance;

/// <summary>
/// Selects a capability verified anchor in the genuine Codex renderer. The native usage label
/// remains preferred; API routes can use the unique header toolbar when that label is absent.
/// </summary>
public static class CodexDesktopBalanceAnchorPolicy
{
    public const string NativeUsageLabelSelector = "[data-ambient-usage-label]";
    public const string HeaderToolbarSelector = "[data-app-shell-header-toolbar]";

    public static string? SelectAnchor(
        CodexDesktopAnchorProbeResult nativeUsageLabel,
        CodexDesktopAnchorProbeResult? headerToolbar)
    {
        ArgumentNullException.ThrowIfNull(nativeUsageLabel);

        if (nativeUsageLabel.IsUniqueVisible)
        {
            return NativeUsageLabelSelector;
        }

        return headerToolbar?.IsUniqueVisible == true
            ? HeaderToolbarSelector
            : null;
    }
}
