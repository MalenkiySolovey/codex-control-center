using Codex.RuntimeKit.Desktop.Compatibility;

namespace CodexSwitcher.App.Features.CodexDesktopBalance;

/// <summary>
/// Codex Desktop versions at or above the first inspected build. Newer builds do not need to be
/// enumerated; each session still requires a unique visible native usage label or header toolbar
/// in the genuine renderer before the extension can be installed.
/// </summary>
public static class CodexDesktopBalanceCompatibilityPolicy
{
    private const string VisibleUsageOrHeaderToolbarContract = "codex-visible-usage-label-or-header-toolbar-v1";
    private static readonly Version MinimumVersion = new(26, 1117, 3838, 0);
    private static readonly Version MaximumVersion = new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);

    public static Version MinimumSupportedVersion => MinimumVersion;

    public static Version MaximumSupportedVersion => MaximumVersion;

    public static bool IsSupportedVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version >= MinimumVersion && version <= MaximumVersion;
    }

    public static CodexDesktopCompatibilityRule CreateVersionRangeRule(
        Version version,
        bool rendererContractVerified)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (!IsSupportedVersion(version))
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Codex Desktop build predates the minimum inspected version.");
        }

        return new CodexDesktopCompatibilityRule(
            MinimumVersion,
            MaximumVersion,
            VisibleUsageOrHeaderToolbarContract,
            RendererContractVerified: rendererContractVerified);
    }
}
