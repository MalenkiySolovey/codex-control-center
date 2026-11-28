using System.Globalization;
using System.Text.Json;
using Esprima;
using Esprima.Ast;

namespace CodexSwitcher.App.Features.CodexDesktopBalance;

/// <summary>Identifies native components by their inputs and relationships in a Rspack bundle.</summary>
internal static class CodexDesktopNativeUsageRecipe
{
    internal const string RailExport = "AmbientUsageRailMenu";
    internal const string RuntimeExport = "__webpack_require__";
    private const string UsageLabels = "__cccUsageLabels";
    private static readonly string[] PopupOptionNames = ["align", "side", "size", "contentWidth"];

    internal sealed record ModuleReference(string Id, int[] Chunks);

    internal sealed record UsageModule(ModuleReference Reference, string Factory);

    internal static bool ExportsRuntime(string source) => Parse(source)?.Body.OfType<ExportNamedDeclaration>().Any(export =>
        export.Specifiers.OfType<ExportSpecifier>().Any(specifier => Name(specifier.Exported) == RuntimeExport) ||
        export.Declaration is FunctionDeclaration function && function.Id?.Name == RuntimeExport) == true;

    internal static UsageModule? FindUsageModule(string source)
    {
        var candidates = from module in ReadModules(source)
                         let export = FindRailExport(module)
                         where export is not null
                         select AdaptUsage(module, export!, source);
        return Unique(candidates);
    }

    internal static ModuleReference? FindReactDomModule(string source) => Unique(ReadModules(source)
        .Where(module => NodesWithin(module.Body).OfType<AssignmentExpression>().Any(assignment =>
            assignment.Left is MemberExpression member && MemberName(member) == "createRoot" &&
            Name(member.Object) == module.Exports && assignment.Right is IFunction))
        .Select(module => module.Reference));

    private static UsageModule? AdaptUsage(Factory module, ObjectExpression export, string source)
    {
        var functions = module.Body.Body.OfType<FunctionDeclaration>().ToArray();
        if (Property(export, RailExport) is not ArrowFunctionExpression { Body: Identifier railName } ||
            Unique(functions.Where(function => function.Id?.Name == railName.Name)) is not { } rail)
        {
            return null;
        }

        // The footer can add wrappers for animation. Find the component that receives the two
        // measurements and keeps their hover labels together. Its markup is owned by Codex.
        var meters = from function in RenderedFunctions(rail, functions)
                     from view in JsxViews(function.Body)
                     where HasProps(view.Props, "primary", "secondary", "interactive")
                     let meter = Unique(functions.Where(item => item.Id?.Name == Name(view.Target)))
                     where meter is not null
                     select (Parent: function, Component: meter!);
        var meterCandidates = meters.Distinct().Take(2).ToArray();
        if (meterCandidates.Length != 1)
        {
            return null;
        }

        var (parent, indicator) = meterCandidates[0];
        var primary = FindMessage(indicator, ".fiveHourRemainingTooltip");
        var secondary = FindMessage(indicator, ".weeklyRemainingTooltip");
        var popup = Unique(JsxViews(rail.Body).Where(view => HasProps(view.Props, "triggerButton", "open", "onOpenChange")));
        var trigger = Unique(JsxViews(rail.Body).Where(view => HasProps(view.Props, "aria-label", "children")));
        var menus = from function in functions
                    from title in JsxViews(function.Body)
                    where title.Target is MemberExpression titleMember && MemberName(titleMember) == "Title"
                    let menu = Reference(((MemberExpression)title.Target).Object)
                    where menu is not null && JsxViews(function.Body).Any(view => IsMemberOf(view.Target, menu, "Section")) &&
                          JsxViews(function.Body).Any(view => IsMemberOf(view.Target, menu, "Separator"))
                    select new Menu(function, menu!);
        var menuView = Unique(menus);
        if (primary is null || secondary is null || NodesWithin(module.Body).OfType<Identifier>().Any(identifier => identifier.Name == UsageLabels) ||
            popup is null || Reference(popup.Target) is not { } popupReference ||
            trigger is null || Reference(trigger.Target) is not { } triggerReference || menuView is null)
        {
            return null;
        }

        var renderMenu = JsxViews(menuView.Function.Body).ToArray();
        var tooltip = Unique(renderMenu.Where(view => HasProps(view.Props, "delayDuration", "children") &&
                view.Target is MemberExpression member && MemberName(member) == "Provider")
            .Select(view => Reference(((MemberExpression)view.Target).Object)));
        var rows = from function in functions
                   where JsxViews(function.Body).Any(view => Text(Property(view.Props, "variant")) == "display" &&
                           Property(view.Props, "row") is not null) &&
                       Calls(menuView.Function.Body).Any(call => call.Arguments is [Identifier row] && row.Name == function.Id?.Name &&
                           call.Callee is MemberExpression member && MemberName(member) == "map")
                   select function.Id!.Name;
        var rowReference = Unique(rows);
        var react = Unique(Calls(rail.Body).Select(call => Unwrap(call.Callee)).OfType<MemberExpression>()
            .Where(member => MemberName(member) == "useState").Select(member => Reference(member.Object)).Distinct());
        var imports = module.Body.Body.OfType<VariableDeclaration>().SelectMany(declaration => declaration.Declarations)
            .Where(item => item.Init is CallExpression call && Name(call.Callee) == module.Require)
            .Select(item => Name(item.Id)).ToHashSet(StringComparer.Ordinal);
        var scopeReferences = Calls(rail.Body).Where(call => call.Arguments is [MemberExpression member] && imports.Contains(Name(member.Object)))
            .Select(call => Reference(call.Arguments[0])).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (tooltip is null || rowReference is null || react is null || scopeReferences.Length == 0)
        {
            return null;
        }

        var glyph = Unique(JsxViews(parent.Body).Where(view => Text(Property(view.Props, "kind")) == "included")
            .Select(view => Reference(view.Target)).Distinct()) ?? "null";
        var popupOptions = PopupOptionNames
            .Select(key => (Key: key, Value: Text(Property(popup.Props, key))))
            .Where(option => option.Value is not null).ToDictionary(option => option.Key, option => option.Value);
        var exports = "{Indicator:()=>" + indicator.Id!.Name + ",Glyph:()=>" + glyph + ",Popup:()=>" + popupReference +
            ",Menu:()=>" + menuView.Reference + ",Row:()=>" + rowReference + ",Trigger:()=>" + triggerReference +
            ",Tooltip:()=>" + tooltip + ",React:()=>" + react + ",Scopes:()=>[" + string.Join(',', scopeReferences) +
            "],PopupOptions:()=> (" + JsonSerializer.Serialize(popupOptions) + ")}";

        var body = source[module.Body.Range.Start..module.Body.Range.End];
        foreach (var (node, replacement) in new (Node, string)[]
                 {
                     (export, exports),
                     (primary, UsageLabels + ".primary"),
                     (secondary, UsageLabels + ".secondary"),
                 }.OrderByDescending(change => change.Item1.Range.Start))
        {
            body = body[..(node.Range.Start - module.Body.Range.Start)] + replacement + body[(node.Range.End - module.Body.Range.Start)..];
        }

        return new(module.Reference, $"function({module.Module},{module.Exports},{module.Require},{UsageLabels}){body}");
    }

    private static CallExpression? FindMessage(FunctionDeclaration function, string suffix) => Unique(JsxViews(function.Body)
        .Where(view => Text(Property(view.Props, "id"))?.EndsWith(suffix, StringComparison.Ordinal) == true)
        .Select(view => view.Call));

    private static IEnumerable<FunctionDeclaration> RenderedFunctions(FunctionDeclaration rail, FunctionDeclaration[] functions)
    {
        var seen = new HashSet<FunctionDeclaration>();
        var pending = new Stack<FunctionDeclaration>();
        pending.Push(rail);
        while (pending.TryPop(out var function))
        {
            if (!seen.Add(function)) continue;

            yield return function;
            var targets = JsxViews(function.Body).Select(view => Name(view.Target)).ToHashSet(StringComparer.Ordinal);
            foreach (var child in functions.Where(child => targets.Contains(child.Id?.Name)))
            {
                pending.Push(child);
            }
        }
    }

    private static ObjectExpression? FindRailExport(Factory module) => Unique(module.Body.Body.OfType<ExpressionStatement>()
        .Select(statement => statement.Expression).OfType<CallExpression>()
        .Where(call => call.Callee is MemberExpression member && Name(member.Object) == module.Require && MemberName(member) == "d" &&
            call.Arguments is [Identifier target, ObjectExpression exports] && target.Name == module.Exports &&
            Property(exports, RailExport) is not null)
        .Select(call => (ObjectExpression)call.Arguments[1]));

    private static IEnumerable<Factory> ReadModules(string source)
    {
        var exports = Parse(source)?.Body.OfType<ExportNamedDeclaration>().Select(export => export.Declaration)
            .OfType<VariableDeclaration>().SelectMany(declaration => declaration.Declarations).ToArray();
        if (exports is null || Unique(exports.Where(item => Name(item.Id) == "__rspack_esm_ids"))?.Init is not ArrayExpression ids ||
            Unique(exports.Where(item => Name(item.Id) == "__webpack_modules__"))?.Init is not ObjectExpression modules)
        {
            yield break;
        }

        var chunkIds = ids.Elements.Select(element => element is Literal { Value: double number } &&
            number is >= 0 and <= int.MaxValue && number == Math.Truncate(number) ? (int)number : -1).ToArray();
        if (chunkIds.Length is 0 or > 16 || chunkIds.Contains(-1))
        {
            yield break;
        }

        foreach (var property in modules.Properties.OfType<Property>())
        {
            if (!property.Computed && ModuleId(property.Key) is { Length: > 0 and <= 64 } id &&
                property.Value is IFunction
                {
                    Async: false, Generator: false, Params: [Identifier module, Identifier output, Identifier require], Body: BlockStatement body,
                })
            {
                yield return new(new(id, chunkIds), body, module.Name, output.Name, require.Name);
            }
        }
    }

    private static Esprima.Ast.Module? Parse(string source)
    {
        try
        {
            return new JavaScriptParser(new ParserOptions { Tolerant = false }).ParseModule(source);
        }
        catch (ParserException)
        {
            return null;
        }
    }

    private static IEnumerable<View> JsxViews(Node root) => Calls(root)
        .Where(call => Unwrap(call.Callee) is MemberExpression member && MemberName(member) is "jsx" or "jsxs" or "createElement" &&
            call.Arguments.Count >= 2 && call.Arguments[1] is ObjectExpression)
        .Select(call => new View(call, call.Arguments[0], (ObjectExpression)call.Arguments[1]));

    private static IEnumerable<CallExpression> Calls(Node root) => NodesWithin(root).OfType<CallExpression>();

    private static IEnumerable<Node> NodesWithin(Node root)
    {
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            yield return node;
            foreach (var child in node.ChildNodes)
            {
                pending.Push(child);
            }
        }
    }

    private static Expression Unwrap(Expression expression) => expression is SequenceExpression { Expressions.Count: 2 } sequence
        ? sequence.Expressions[1]
        : expression;

    private static bool HasProps(ObjectExpression value, params string[] keys) => keys.All(key => Property(value, key) is not null);

    private static Expression? Property(ObjectExpression value, string key) => Unique(value.Properties.OfType<Property>()
        .Where(property => !property.Computed && Name(property.Key) == key))?.Value as Expression;

    private static string? Name(Node? node) => node is Identifier identifier ? identifier.Name : Text(node);

    private static string? ModuleId(Node key) => key is Literal { Value: double number } &&
        number is >= 0 and <= int.MaxValue && number == Math.Truncate(number)
        ? ((int)number).ToString(CultureInfo.InvariantCulture)
        : Name(key);

    private static string? Text(Node? node) => node is Literal { Value: string text } ? text : null;

    private static string? MemberName(MemberExpression member) => member.Computed ? Text(member.Property) : Name(member.Property);

    private static string? Reference(Node node) => node switch
    {
        Identifier identifier => identifier.Name,
        MemberExpression { Optional: false } member when Reference(member.Object) is { } owner && MemberName(member) is { } name =>
            owner + "[" + JsonSerializer.Serialize(name) + "]",
        _ => null,
    };

    private static bool IsMemberOf(Node target, string owner, string name) => target is MemberExpression member &&
        MemberName(member) == name && Reference(member.Object) == owner;

    private static T? Unique<T>(IEnumerable<T?> values) where T : class
    {
        using var enumerator = values.GetEnumerator();
        return enumerator.MoveNext() && enumerator.Current is { } first && !enumerator.MoveNext() ? first : null;
    }

    private sealed record Factory(ModuleReference Reference, BlockStatement Body, string Module, string Exports, string Require);

    private sealed record View(CallExpression Call, Expression Target, ObjectExpression Props);

    private sealed record Menu(FunctionDeclaration Function, string Reference);
}
