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
        Assert.Contains("[data-app-shell-header-toolbar]", script, StringComparison.Ordinal);
        Assert.Contains("uniqueVisible(nativeAnchor)", script, StringComparison.Ordinal);
        Assert.Contains("uniqueVisible(toolbarAnchor)", script, StringComparison.Ordinal);
        Assert.Contains("bg-token-bg-secondary", script, StringComparison.Ordinal);
        Assert.Contains("text-token-text-secondary", script, StringComparison.Ordinal);
        Assert.Contains("border-token-border-default", script, StringComparison.Ordinal);
        Assert.Contains("rounded-full", script, StringComparison.Ordinal);
        Assert.Contains("aria-controls", script, StringComparison.Ordinal);
        Assert.Contains("aria-expanded", script, StringComparison.Ordinal);
        Assert.Contains("""setProperty("-webkit-app-region", "no-drag")""", script, StringComparison.Ordinal);
        Assert.Contains("document.body.append(panel)", script, StringComparison.Ordinal);
        Assert.Contains("panel.showPopover", script, StringComparison.Ordinal);
        Assert.Contains("data-codex-control-center-api-balance", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BundledRendererUsesTextOnlyAndReleasesOwnedDomAndListeners()
    {
        var script = CodexDesktopBalanceRendererExtension.LoadJavaScript();

        Assert.Contains("textContent", script, StringComparison.Ordinal);
        Assert.Contains("listeners.abort()", script, StringComparison.Ordinal);
        Assert.Contains("surface?.root.remove()", script, StringComparison.Ordinal);
        Assert.Contains("surface?.panel.remove()", script, StringComparison.Ordinal);
        Assert.Contains("setTimeout(removeSurface, leaseMilliseconds)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("MutationObserver", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eval(", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fetch(", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("XMLHttpRequest", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WebSocket", script, StringComparison.OrdinalIgnoreCase);
    }
}
