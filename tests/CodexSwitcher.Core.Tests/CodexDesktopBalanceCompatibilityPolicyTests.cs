using Codex.RuntimeKit.Desktop.Compatibility;
using CodexSwitcher.App.Features.CodexDesktopBalance;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexDesktopBalanceCompatibilityPolicyTests
{
    [Theory]
    [InlineData("26.1117.3838.0")]
    [InlineData("26.1117.4957.0")]
    [InlineData("27.0.0.0")]
    public void NewerBuildsNeedNoVersionListEntryAndStillRequireTheLiveVisibleAnchor(string versionText)
    {
        var version = new Version(versionText);

        Assert.True(CodexDesktopBalanceCompatibilityPolicy.IsSupportedVersion(version));

        var verified = new CodexDesktopCompatibilityMatrix(
        [CodexDesktopBalanceCompatibilityPolicy.CreateVersionRangeRule(version, rendererContractVerified: true)])
            .Evaluate(version);
        var unverified = new CodexDesktopCompatibilityMatrix(
        [CodexDesktopBalanceCompatibilityPolicy.CreateVersionRangeRule(version, rendererContractVerified: false)])
            .Evaluate(version);

        Assert.Equal(CodexDesktopCompatibilityState.Supported, verified.State);
        Assert.NotEqual(CodexDesktopCompatibilityState.Supported, unverified.State);
    }

    [Fact]
    public void BuildBeforeMinimumInspectedVersionIsNotSupported()
    {
        var version = new Version(26, 1117, 3837, 0);

        Assert.False(CodexDesktopBalanceCompatibilityPolicy.IsSupportedVersion(version));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CodexDesktopBalanceCompatibilityPolicy.CreateVersionRangeRule(version, rendererContractVerified: true));
    }
}
