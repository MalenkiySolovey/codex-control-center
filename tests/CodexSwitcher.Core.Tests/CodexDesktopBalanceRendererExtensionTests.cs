using CodexSwitcher.App.Features.CodexDesktopBalance;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexDesktopBalanceRendererExtensionTests
{
    [Fact]
    public void BundledRendererUsesReviewedHashAndCodexThemeTokens()
    {
        var script = CodexDesktopBalanceRendererExtension.LoadJavaScript();

        Assert.True(CodexDesktopBalanceRendererExtension.MatchesApprovedHash(script));
        Assert.Contains("[data-ambient-usage-label]", script, StringComparison.Ordinal);
        Assert.Contains("bg-token-bg-secondary", script, StringComparison.Ordinal);
        Assert.Contains("text-token-text-secondary", script, StringComparison.Ordinal);
        Assert.Contains("border-token-border-default", script, StringComparison.Ordinal);
        Assert.Contains("rounded-full", script, StringComparison.Ordinal);
        Assert.Contains("maxAnchorCandidates = 16", script, StringComparison.Ordinal);
        Assert.Contains("maxMutationRecords = 256", script, StringComparison.Ordinal);
        Assert.Contains("maxNodesPerMutation = 512", script, StringComparison.Ordinal);
        Assert.Contains("attributeFilter", script, StringComparison.Ordinal);
        Assert.Contains("aria-controls", script, StringComparison.Ordinal);
        Assert.Contains("data-codex-control-center-api-balance", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BundledRendererUsesTextOnlyAndReleasesOwnedDomAndListeners()
    {
        var script = CodexDesktopBalanceRendererExtension.LoadJavaScript();

        Assert.Contains("textContent", script, StringComparison.Ordinal);
        Assert.Contains("observer?.disconnect()", script, StringComparison.Ordinal);
        Assert.Contains("removeEventListener", script, StringComparison.Ordinal);
        Assert.Contains("root?.remove()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eval(", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fetch(", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("XMLHttpRequest", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WebSocket", script, StringComparison.OrdinalIgnoreCase);
    }
}
