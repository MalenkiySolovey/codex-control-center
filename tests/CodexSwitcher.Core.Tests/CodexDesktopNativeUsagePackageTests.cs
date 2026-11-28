using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexSwitcher.App.Features.CodexDesktopBalance;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexDesktopNativeUsagePackageTests
{
    private const string Runtime = "const loader = () => {}; export { loader as __webpack_require__ };";
    private const string ReactDom = "/* react-dom-client */ export const __rspack_esm_ids=[404]; export const __webpack_modules__={ mount(m,exports,load){exports.createRoot=function(target){};} };";
    private const string MeterTemplate = """
        export const __rspack_esm_ids=[CHUNK_ID];
        export const __webpack_modules__={
          MODULE_ID(module, output, load) {
            load.d(output, {AmbientUsageRailMenu:()=>ProfileLimits});
            const scope = load('scope-config'), react = load('react-id');
            function ProfileLimits(props) {
              const [open] = react.useState(false);
              useScope(scope.app);
              const readout = jsx.jsx(UsageGraphic, {});
              const trigger = jsx.jsx(button.Control, { 'aria-label': 'Usage', children: readout });
              return jsx.jsx(native.Popup, {contentWidth:'menuReadout', side:'right', triggerButton:trigger, open:open, onOpenChange:change});
            }
            function UsageGraphic() {
              const glyph = jsx.jsx(LimitSymbol, {kind:'included'});
              return jsx.jsx(INDICATOR, {primary:12, secondary:30, interactive:true, children:glyph});
            }
            function LimitSymbol(props) { return props.kind; }
            function INDICATOR(input) {
              const daily = (() => {
                const input = 12;
                return jsx.jsx(Message, { id:'codex.ambientUsage.meter.fiveHourRemainingTooltip', values:{ remaining:input } });
              })();
              const weekly = jsx.jsx(Message, { id:'codex.ambientUsage.meter.weeklyRemainingTooltip' });
              return jsx.jsx(MARKUP, { children:[daily,weekly] });
            }
            function Details(props) {
              const title = jsx.jsx(popup.Menu.Title, {children:props.label});
              const divider = jsx.jsx(popup.Menu.Separator, {});
              const section = jsx.jsx(popup.Menu.Section, {children:props.rows.map(RowOfUsage)});
              return jsx.jsx(tooltip.Hover.Provider, {delayDuration:350, children:[title,divider,section]});
            }
            function RowOfUsage(row) { return jsx.jsx(NativeRow, {row:row, variant:'display'}); }
          },
          irrelevant(module, output, load) { throw new Error('unrelated module'); }
        };
        """;

    [Theory]
    [InlineData("entry-old-hash.js", 777, "shortId", "RadialIndicator", "'svg'")]
    [InlineData("entry-new-hash.js", 1002, "1099", "LinearIndicator", "'progress'")]
    public async Task FindsComponentsAcrossChangedNamesAndNativeMarkup(string entry, int chunk, string module, string indicator, string markup)
    {
        var meter = Meter(chunk, module, indicator, markup);
        using var archive = Archive(entry, ("assets/lazy/unrelated-name.js", meter));

        var inspected = await CodexDesktopNativeUsagePackage.InspectArchiveAsync(archive, CancellationToken.None);

        Assert.Equal(CodexDesktopNativeUsagePackage.State.Ready, inspected.State);
        var result = inspected.Script;
        Assert.NotNull(result);
        Assert.Contains($"\"entry\":\"/assets/{entry}\"", result, StringComparison.Ordinal);
        Assert.Contains($"\"usage\":\"{module}\"", result, StringComparison.Ordinal);
        Assert.Contains($"\"chunks\":[{chunk},404]", result, StringComparison.Ordinal);
        Assert.Contains($"Indicator:()=>{indicator}", result, StringComparison.Ordinal);
        Assert.Contains($"jsx.jsx({markup},", result, StringComparison.Ordinal);
        Assert.Contains("function(module,output,load,__cccUsageLabels)", result, StringComparison.Ordinal);
        Assert.Contains("return __cccUsageLabels.primary", result, StringComparison.Ordinal);
        Assert.Contains("const weekly = __cccUsageLabels.secondary", result, StringComparison.Ordinal);
        Assert.Contains("Row:()=>RowOfUsage", result, StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated module", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsCompetingUsageModules()
    {
        using var archive = Archive("entry.js",
            ("assets/first.js", Meter(7, "first", "Indicator", "'svg'")),
            ("assets/second.js", Meter(9, "second", "OtherIndicator", "'div'")));

        var result = await CodexDesktopNativeUsagePackage.InspectArchiveAsync(archive, CancellationToken.None);
        Assert.Equal(CodexDesktopNativeUsagePackage.State.AmbiguousContract, result.State);
        Assert.Null(result.Script);
    }

    [Fact]
    public async Task RejectsMeterWithMissingOrUnclearLabels()
    {
        var meter = Meter(7, "module", "Indicator", "'div'");
        var missing = meter.Replace("weeklyRemainingTooltip", "differentTooltip", StringComparison.Ordinal);
        var ambiguous = meter.Replace("weeklyRemainingTooltip", "fiveHourRemainingTooltip", StringComparison.Ordinal);
        using var archive = Archive("entry.js", ("assets/module.js", missing));
        using var second = Archive("entry.js", ("assets/module.js", ambiguous));

        foreach (var fixture in new[] { archive, second })
        {
            var result = await CodexDesktopNativeUsagePackage.InspectArchiveAsync(fixture, CancellationToken.None);
            Assert.Equal(CodexDesktopNativeUsagePackage.State.UnsupportedContract, result.State);
            Assert.Null(result.Script);
        }
    }

    [Fact]
    public async Task CorruptedSourceCannotGenerateAnAdapter()
    {
        using var archive = Archive("entry.js", ("assets/module.js", Meter(7, "fixture", "Indicator", "'svg'")));
        archive.Position = archive.Length - 1;
        archive.WriteByte((byte)'!');
        archive.Position = 0;

        var result = await CodexDesktopNativeUsagePackage.InspectArchiveAsync(archive, CancellationToken.None);

        Assert.Equal(CodexDesktopNativeUsagePackage.State.CorruptSource, result.State);
        Assert.Null(result.Script);
    }

    private static string Meter(int chunk, string module, string indicator, string markup) => MeterTemplate
        .Replace("CHUNK_ID", chunk.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("MODULE_ID", module, StringComparison.Ordinal)
        .Replace("INDICATOR", indicator, StringComparison.Ordinal)
        .Replace("MARKUP", markup, StringComparison.Ordinal);

    private static MemoryStream Archive(string runtime, params (string Path, string Source)[] modules)
    {
        using var data = new MemoryStream();
        var root = new Dictionary<string, object>();
        foreach (var (path, source) in modules.Prepend(($"assets/{runtime}", Runtime))
                     .Prepend(("assets/react.js", ReactDom))
                     .Prepend(("index.html", $"<script src='/assets/{runtime}' type='module'></script>")))
        {
            var scope = root;
            var components = ("webview/" + path).Split('/');
            foreach (var directory in components[..^1])
            {
                if (!scope.TryGetValue(directory, out var subtree))
                {
                    subtree = new Dictionary<string, object> { ["files"] = new Dictionary<string, object>() };
                    scope[directory] = subtree;
                }
                scope = (Dictionary<string, object>)((Dictionary<string, object>)subtree)["files"];
            }

            var bytes = Encoding.UTF8.GetBytes(source);
            scope[components[^1]] = new
            {
                size = bytes.Length,
                offset = data.Position.ToString(CultureInfo.InvariantCulture),
                integrity = new { algorithm = "SHA256", hash = Convert.ToHexString(SHA256.HashData(bytes)) },
            };
            data.Write(bytes);
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(new { files = root });
        var archive = new MemoryStream();
        using (var writer = new BinaryWriter(archive, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(4);
            writer.Write(8 + json.Length);
            writer.Write(4 + json.Length);
            writer.Write(json.Length);
            writer.Write(json);
            writer.Write(data.ToArray());
        }
        archive.Position = 0;
        return archive;
    }
}
