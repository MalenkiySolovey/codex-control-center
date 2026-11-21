using Codex.RuntimeKit.Desktop.DebugTransport;
using CodexSwitcher.App.Features.CodexDesktopBalance;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexDesktopBalanceAnchorPolicyTests
{
    [Fact]
    public void NativeUsageLabelWinsWhenBothAnchorsAreUniqueAndVisible()
    {
        var selected = CodexDesktopBalanceAnchorPolicy.SelectAnchor(
            Probe(totalCount: 1, visibleCount: 1),
            Probe(totalCount: 1, visibleCount: 1));

        Assert.Equal(CodexDesktopBalanceAnchorPolicy.NativeUsageLabelSelector, selected);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    public void UniqueVisibleHeaderToolbarIsFallbackWhenNativeUsageLabelIsUnavailable(
        int nativeTotalCount,
        int nativeVisibleCount)
    {
        var selected = CodexDesktopBalanceAnchorPolicy.SelectAnchor(
            Probe(nativeTotalCount, nativeVisibleCount),
            Probe(totalCount: 1, visibleCount: 1));

        Assert.Equal(CodexDesktopBalanceAnchorPolicy.HeaderToolbarSelector, selected);
    }

    [Fact]
    public void MissingOrAmbiguousAnchorsFailClosed()
    {
        var missing = CodexDesktopBalanceAnchorPolicy.SelectAnchor(
            Probe(totalCount: 0, visibleCount: 0),
            Probe(totalCount: 0, visibleCount: 0));
        var ambiguousFallback = CodexDesktopBalanceAnchorPolicy.SelectAnchor(
            Probe(totalCount: 2, visibleCount: 2),
            Probe(totalCount: 2, visibleCount: 2));

        Assert.Null(missing);
        Assert.Null(ambiguousFallback);
    }

    private static CodexDesktopAnchorProbeResult Probe(int totalCount, int visibleCount) =>
        new(totalCount, visibleCount, TagName: null, ParentTagName: null, ParentClassNames: null);
}
