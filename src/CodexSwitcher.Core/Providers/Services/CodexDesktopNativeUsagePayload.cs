using System.Text.Json.Serialization;

namespace CodexSwitcher.Core.Providers.Services;

/// <summary>Presentation values for the version-matched Codex usage components.</summary>
public sealed record CodexDesktopNativeUsagePayload(
    [property: JsonPropertyName("compact")] string Compact,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("percent")] decimal? RemainingPercentage,
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

        var percentage = presentation.Kind == CodexDesktopBalanceKind.ApiKeyAllowance &&
            !presentation.QuotaUnlimited && presentation.Remaining.HasValue && presentation.Maximum > 0m &&
            presentation.RemainingPercentage is >= 0m and <= 100m
                ? presentation.RemainingPercentage
                : null;
        return new(compact, title, percentage, rows);
    }
}

public sealed record CodexDesktopNativeUsageRow(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string Value);
