using System.Text.Json.Serialization;

namespace CodexSwitcher.Core.Providers.Services;

/// <summary>
/// Sanitized, presentation-only strings sent to the Codex Desktop renderer.
/// No provider identity, route, credential, URL, fingerprint, or raw metric is serialized.
/// </summary>
public sealed record CodexDesktopBalanceRendererPayload(
    [property: JsonPropertyName("compact")] string Compact,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("lines")] IReadOnlyList<string> Lines)
{
    public const int MaximumCompactLength = 192;
    public const int MaximumTitleLength = 96;
    public const int MaximumLines = 12;
    public const int MaximumLineLength = 192;

    public static CodexDesktopBalanceRendererPayload? FromPresentation(
        CodexDesktopBalancePresentation presentation,
        bool russian)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (presentation.Kind == CodexDesktopBalanceKind.Hidden)
        {
            return null;
        }

        var compact = SafeDisplayText(presentation.ToCompactText(russian), MaximumCompactLength);
        var title = SafeDisplayText(presentation.ToExpandedTitle(russian), MaximumTitleLength);
        if (compact is null || title is null)
        {
            return null;
        }

        var lines = presentation.ToExpandedLines(russian)
            .Take(MaximumLines)
            .Select(line => SafeDisplayText(line, MaximumLineLength))
            .Where(line => line is not null)
            .Cast<string>()
            .ToArray();

        return new CodexDesktopBalanceRendererPayload(compact, title, lines);
    }

    internal static string? SafeDisplayText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            return null;
        }

        return value.Trim();
    }
}
