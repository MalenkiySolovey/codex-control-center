using System.Globalization;
using System.Text.Json.Serialization;
using Llm.ProviderKit.Inspection;

namespace CodexSwitcher.Core.Providers.Services;

/// <summary>Presentation values for the version-matched Codex usage components.</summary>
public sealed record CodexDesktopNativeUsagePayload(
    [property: JsonPropertyName("compact")] string Compact,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("upperArc")] CodexDesktopNativeUsageArc UpperArc,
    [property: JsonPropertyName("lowerArc")] CodexDesktopNativeUsageArc LowerArc,
    [property: JsonPropertyName("rows")] IReadOnlyList<CodexDesktopNativeUsageRow> Rows)
{
    public const int MaximumRows = 16;

    public static CodexDesktopNativeUsagePayload? FromPresentation(CodexDesktopBalancePresentation presentation, bool russian)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (presentation.Kind == CodexDesktopBalanceKind.Hidden)
        {
            return null;
        }

        var compact = CodexDesktopBalanceRendererPayload.SafeDisplayText(
            presentation.ToCompactText(russian), CodexDesktopBalanceRendererPayload.MaximumCompactLength);
        var title = CodexDesktopBalanceRendererPayload.SafeDisplayText(
            presentation.ToExpandedTitle(russian), CodexDesktopBalanceRendererPayload.MaximumTitleLength);
        if (compact is null || title is null)
        {
            return null;
        }

        var rows = new List<CodexDesktopNativeUsageRow>();
        foreach (var detail in presentation.ToExpandedDetails(russian))
        {
            var label = CodexDesktopBalanceRendererPayload.SafeDisplayText(detail.Label, CodexDesktopBalanceRendererPayload.MaximumLineLength);
            var value = CodexDesktopBalanceRendererPayload.SafeDisplayText(detail.Value, CodexDesktopBalanceRendererPayload.MaximumLineLength);
            if (label is not null && (detail.Value is null || value is not null))
            {
                rows.Add(new(label, value ?? string.Empty));
            }

            if (rows.Count == MaximumRows)
            {
                break;
            }
        }

        var culture = russian ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.GetCultureInfo("en-US");
        CodexDesktopNativeUsageArc upper;
        CodexDesktopNativeUsageArc lower;
        if (presentation.Kind == CodexDesktopBalanceKind.ApiKeyAllowance && !presentation.QuotaUnlimited)
        {
            var percentage = presentation.Remaining.HasValue && presentation.Maximum > 0m &&
                presentation.RemainingPercentage is >= 0m and <= 100m
                    ? presentation.RemainingPercentage
                    : null;
            upper = Arc(russian ? "Осталось" : "Remaining", presentation.Remaining, presentation.UnitCode,
                presentation.RemainingDisplay, percentage);
            lower = Arc(russian ? "Лимит ключа" : "Key limit", presentation.Maximum, presentation.UnitCode,
                presentation.MaximumDisplay, presentation.Maximum > 0m ? 100m : null);
        }
        else
        {
            upper = Arc(russian ? "Баланс аккаунта" : "Account balance", presentation.AccountBalance,
                presentation.Kind == CodexDesktopBalanceKind.AccountBalance ? presentation.UnitCode : presentation.AccountBalanceUnitCode,
                display: null, fill: null);
            var hasScopedUsage = presentation.Kind == CodexDesktopBalanceKind.UsageOnly &&
                presentation.ScopeLabel == nameof(ProviderMetricScope.ApiKey);
            lower = Arc(russian ? "Потрачено ключом" : "Key usage",
                hasScopedUsage ? presentation.Usage : presentation.KeyUsage,
                hasScopedUsage ? presentation.UnitCode : presentation.KeyUsageUnitCode,
                hasScopedUsage ? presentation.UsageDisplay : presentation.KeyUsageDisplay, fill: null);
        }

        return new(compact, title, upper, lower, rows);

        CodexDesktopNativeUsageArc Arc(string label, decimal? amount, string? unitCode,
            ProviderMetricDisplay? display, decimal? fill)
        {
            var unknown = russian ? "неизвестно" : "Unknown";
            var value = amount is { } observed
                ? CodexDesktopBalancePresentation.FormatAmount(observed, unitCode, culture, russian, display)
                : unknown;
            var tooltip = CodexDesktopBalanceRendererPayload.SafeDisplayText(
                $"{label} · {value}", CodexDesktopBalanceRendererPayload.MaximumCompactLength);
            return tooltip is null ? new($"{label} · {unknown}", null) : new(tooltip, fill);
        }
    }
}

public sealed record CodexDesktopNativeUsageArc(
    [property: JsonPropertyName("tooltip")] string Tooltip,
    [property: JsonPropertyName("fill")] decimal? FillPercentage);

public sealed record CodexDesktopNativeUsageRow(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string Value);
