using System.Text.Json;

namespace CodexSwitcher.Core.Providers.Catalog;

/// <summary>Control Center catalog adapter over the generic match implementation in ProviderKit.</summary>
public static class ProviderMatcher
{
    public static string? NormalizeHost(string? input) => Llm.ProviderKit.Catalog.ProviderMatcher.NormalizeHost(input);

    public static string? NormalizeBaseUrl(string? input) => Llm.ProviderKit.Catalog.ProviderMatcher.NormalizeBaseUrl(input);

    public static bool Matches(ProviderDescriptor descriptor, string? input) =>
        Llm.ProviderKit.Catalog.ProviderMatcher.Matches(Convert(descriptor), input);

    public static ProviderDescriptor? FindBestMatch(ProviderCatalog catalog, string? input)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var genericCatalog = JsonSerializer.Deserialize<Llm.ProviderKit.Catalog.ProviderCatalog>(
            JsonSerializer.SerializeToUtf8Bytes(catalog));
        var match = genericCatalog is null
            ? null
            : Llm.ProviderKit.Catalog.ProviderMatcher.FindBestMatch(genericCatalog, input);
        return match is null
            ? null
            : catalog.Providers.FirstOrDefault(provider => string.Equals(provider.Id, match.Id, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsHostTrusted(ProviderDescriptor descriptor, Uri targetUri) =>
        Llm.ProviderKit.Catalog.ProviderMatcher.IsHostTrusted(Convert(descriptor), targetUri);

    private static Llm.ProviderKit.Catalog.ProviderDescriptor Convert(ProviderDescriptor descriptor) =>
        JsonSerializer.Deserialize<Llm.ProviderKit.Catalog.ProviderDescriptor>(JsonSerializer.SerializeToUtf8Bytes(descriptor))
        ?? throw new InvalidOperationException("The provider descriptor could not be mapped to ProviderKit.");
}
