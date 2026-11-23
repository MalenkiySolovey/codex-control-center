using Codex.RuntimeKit.Desktop.Discovery;
using CodexSwitcher.App.Features.CodexDesktopBalance;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexDesktopNativeUsagePackageTests
{
    private const string Template = "(() => { const factory = /* CODEX_NATIVE_USAGE_FACTORY */ null; })()";
    private const string Source = """
        export const __webpack_modules__={w5W4(e,n,i){i.d(n,{AmbientUsageRailMenu:()=>K});function W(e){let E=B&&null!=P;}function R(e){}function G(e,n){}function K(){return {contentWidth:"menuReadout"};}},mBEO(e,n,i){}};
        """;

    [Fact]
    public void NativeRecipeExposesOriginalComponentBodies()
    {
        var script = CodexDesktopNativeUsagePackage.TryBuildScript(Source, Template);

        Assert.NotNull(script);
        Assert.Contains("Ring:()=>W", script, StringComparison.Ordinal);
        Assert.Contains("Popup:()=>u.Ab", script, StringComparison.Ordinal);
        Assert.Contains("r=e.primaryTooltip", script, StringComparison.Ordinal);
        Assert.Contains("r=e.secondaryTooltip", script, StringComparison.Ordinal);
        Assert.Contains("let E=B&&null!=P;}function R(e){}function G(e,n){}", script, StringComparison.Ordinal);
        Assert.DoesNotContain("function mBEO", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeRecipeRejectsMissingAndAmbiguousAnchors()
    {
        Assert.Null(CodexDesktopNativeUsagePackage.TryBuildScript(Source, Template + Template));
        Assert.Null(CodexDesktopNativeUsagePackage.TryBuildScript(Source + Source, Template));
        Assert.Null(CodexDesktopNativeUsagePackage.TryBuildScript(Source.Replace("menuReadout", "menu", StringComparison.Ordinal), Template));
        Assert.Null(CodexDesktopNativeUsagePackage.TryBuildScript(Source.Replace("AmbientUsageRailMenu", "different", StringComparison.Ordinal), Template));
        Assert.Null(CodexDesktopNativeUsagePackage.TryBuildScript(Source.Replace("let E=B&&null!=P;", "", StringComparison.Ordinal), Template));
        Assert.Null(CodexDesktopNativeUsagePackage.TryBuildScript(Source.Replace("let E=B&&null!=P;", "let E=B&&null!=P;let E=B&&null!=P;", StringComparison.Ordinal), Template));
    }

    [Theory]
    [InlineData("26.1117.4957.1", "OpenAI.Codex_2p2nqsd0c76g0")]
    [InlineData("26.1117.4957.0", "Different.Publisher")]
    public async Task NativeRecipeRejectsUnknownInstallations(string version, string packageFamily)
    {
        var installation = new CodexDesktopInstallation(
            "OpenAI.Codex", packageFamily, packageFamily + "!App", "unused", "unused", Version.Parse(version));

        Assert.Null(await CodexDesktopNativeUsagePackage.TryLoadAsync(installation, CancellationToken.None));
    }
}
