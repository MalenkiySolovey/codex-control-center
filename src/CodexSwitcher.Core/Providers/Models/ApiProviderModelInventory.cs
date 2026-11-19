using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Providers.Models;

public enum ModelDiscoverySource
{
    CatalogKnown = 0,
    Discovered = 1,
    Manual = 2,
    Mixed = 3,
}

public enum ModelAvailability
{
    Unknown = 0,
    Reported = 1,
    NotReported = 2,
}

public enum ModelDiscoveryStatus
{
    Unknown = 0,
    Discovered = 1,
    Manual = 2,
    Mixed = 3,
}

/// <summary>
/// Cosmetic-only formatter for model slugs. It deliberately does not infer
/// capabilities, limits, provider identity, or any other model fact.
/// </summary>
public static class ModelDisplayName
{
    public static string FromSlug(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        // Preserve an exact slug when it contains characters for which a
        // "humanized" rendering could be misleading. This is presentation
        // only; persistence always retains <paramref name="slug"/> verbatim.
        var result = slug
            .Replace('-', ' ')
            .Replace('_', ' ');

        // Keep version-like and suffix segments intact while making familiar
        // family names readable. No capability facts are derived here.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?<!^)(?=[A-Z])",
            " ");

        var words = result.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return slug;
        }

        return string.Join(" ", words.Select(word =>
        {
            if (word.Length == 0 || word.StartsWith(':'))
            {
                return word;
            }

            if (word.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
            {
                return "DeepSeek";
            }

            if (word.Length > 1 && word[0] is 'v' or 'V' && char.IsDigit(word[1]))
            {
                return "v" + word[1..];
            }

            if (word.Length == 1 && char.IsLetter(word[0]))
            {
                return word.ToUpperInvariant();
            }

            return char.ToUpperInvariant(word[0]) + word[1..];
        }));
    }
}

/// <summary>
/// Counts the meaningful changes made while merging a GET /models response.
/// </summary>
public sealed record ModelInventoryMergeResult(
    int ModelsAdded,
    int ModelsUpdated,
    int ManualModelsPreserved,
    int ReportedCount);

/// <summary>
/// Represents a single model item in an API provider's inventory.
/// </summary>
public sealed class ApiProviderModelItem
{
    public string Slug { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; } = true;
    public ModelDiscoverySource DiscoverySource { get; set; } = ModelDiscoverySource.CatalogKnown;
    public ModelAvailability Availability { get; set; } = ModelAvailability.Unknown;
    public DateTimeOffset? LastSeenAt { get; set; }
    public long? ContextWindow { get; set; }
    public string? ContextEvidence { get; set; }
    public ModelCapabilities? Capabilities { get; set; }
    public CodexModelOverrides? UserOverrides { get; set; }
}

/// <summary>
/// Model inventory associated with an <see cref="ApiProviderProfile"/>.
/// Stores discovered and manually registered models for this profile.
/// Strictly persisted in Switchboard app data, NOT in Codex config.toml.
/// </summary>
public sealed class ApiProviderModelInventory
{
    public List<ApiProviderModelItem> Models { get; set; } = new();
    public string? SelectedModel { get; set; }
    public ModelDiscoveryStatus DiscoveryStatus { get; set; } = ModelDiscoveryStatus.Unknown;
    public DateTimeOffset? LastDiscoveryAt { get; set; }

    /// <summary>
    /// Gets all models currently enabled for active routing catalog generation.
    /// </summary>
    public IEnumerable<ApiProviderModelItem> GetEnabledModels()
    {
        return Models.FindAll(m => m.Enabled);
    }

    /// <summary>
    /// Merges an incoming list of discovered model slugs from the API provider.
    /// Preserves existing manual models and existing configurations, marking missing discovered models as NotReported.
    /// </summary>
    public ModelInventoryMergeResult MergeDiscoveredModels(
        IEnumerable<string>? discoveredSlugs,
        DateTimeOffset? observedAt = null)
    {
        var external = new Llm.ProviderKit.Models.ProviderModelInventory
        {
            SelectedModelId = SelectedModel,
            DiscoveryStatus = ToProviderKit(DiscoveryStatus),
            LastDiscoveryAt = LastDiscoveryAt,
            Models = Models.Select(model => new Llm.ProviderKit.Models.ProviderModelItem
            {
                Id = model.Slug,
                DisplayName = model.DisplayName,
                Enabled = model.Enabled,
                DiscoverySource = ToProviderKit(model.DiscoverySource),
                Availability = ToProviderKit(model.Availability),
                LastSeenAt = model.LastSeenAt,
            }).ToList(),
        };

        var result = external.MergeDiscoveredModels(discoveredSlugs, observedAt);
        foreach (var externalModel in external.Models)
        {
            var existing = Models.FirstOrDefault(model => string.Equals(model.Slug, externalModel.Id, StringComparison.Ordinal));
            if (existing is null)
            {
                Models.Add(new ApiProviderModelItem
                {
                    Slug = externalModel.Id,
                    DisplayName = externalModel.DisplayName,
                    Enabled = externalModel.Enabled,
                    DiscoverySource = FromProviderKit(externalModel.DiscoverySource),
                    Availability = FromProviderKit(externalModel.Availability),
                    LastSeenAt = externalModel.LastSeenAt,
                });
                continue;
            }

            existing.DiscoverySource = FromProviderKit(externalModel.DiscoverySource);
            existing.Availability = FromProviderKit(externalModel.Availability);
            existing.LastSeenAt = externalModel.LastSeenAt;
        }

        DiscoveryStatus = FromProviderKit(external.DiscoveryStatus);
        LastDiscoveryAt = external.LastDiscoveryAt;
        return new ModelInventoryMergeResult(result.ModelsAdded, result.ModelsUpdated,
            result.ManualModelsPreserved, result.ReportedCount);
    }

    private static Llm.ProviderKit.Models.ModelDiscoverySource ToProviderKit(ModelDiscoverySource value) => value switch
    {
        ModelDiscoverySource.Discovered => Llm.ProviderKit.Models.ModelDiscoverySource.Discovered,
        ModelDiscoverySource.Manual => Llm.ProviderKit.Models.ModelDiscoverySource.Manual,
        ModelDiscoverySource.Mixed => Llm.ProviderKit.Models.ModelDiscoverySource.Mixed,
        _ => Llm.ProviderKit.Models.ModelDiscoverySource.CatalogKnown,
    };

    private static ModelDiscoverySource FromProviderKit(Llm.ProviderKit.Models.ModelDiscoverySource value) => value switch
    {
        Llm.ProviderKit.Models.ModelDiscoverySource.Discovered => ModelDiscoverySource.Discovered,
        Llm.ProviderKit.Models.ModelDiscoverySource.Manual => ModelDiscoverySource.Manual,
        Llm.ProviderKit.Models.ModelDiscoverySource.Mixed => ModelDiscoverySource.Mixed,
        _ => ModelDiscoverySource.CatalogKnown,
    };

    private static Llm.ProviderKit.Models.ModelAvailability ToProviderKit(ModelAvailability value) => value switch
    {
        ModelAvailability.Reported => Llm.ProviderKit.Models.ModelAvailability.Reported,
        ModelAvailability.NotReported => Llm.ProviderKit.Models.ModelAvailability.NotReported,
        _ => Llm.ProviderKit.Models.ModelAvailability.Unknown,
    };

    private static ModelAvailability FromProviderKit(Llm.ProviderKit.Models.ModelAvailability value) => value switch
    {
        Llm.ProviderKit.Models.ModelAvailability.Reported => ModelAvailability.Reported,
        Llm.ProviderKit.Models.ModelAvailability.NotReported => ModelAvailability.NotReported,
        _ => ModelAvailability.Unknown,
    };

    private static Llm.ProviderKit.Models.ModelDiscoveryStatus ToProviderKit(ModelDiscoveryStatus value) => value switch
    {
        ModelDiscoveryStatus.Discovered => Llm.ProviderKit.Models.ModelDiscoveryStatus.Discovered,
        ModelDiscoveryStatus.Manual => Llm.ProviderKit.Models.ModelDiscoveryStatus.Manual,
        ModelDiscoveryStatus.Mixed => Llm.ProviderKit.Models.ModelDiscoveryStatus.Mixed,
        _ => Llm.ProviderKit.Models.ModelDiscoveryStatus.Unknown,
    };

    private static ModelDiscoveryStatus FromProviderKit(Llm.ProviderKit.Models.ModelDiscoveryStatus value) => value switch
    {
        Llm.ProviderKit.Models.ModelDiscoveryStatus.Discovered => ModelDiscoveryStatus.Discovered,
        Llm.ProviderKit.Models.ModelDiscoveryStatus.Manual => ModelDiscoveryStatus.Manual,
        Llm.ProviderKit.Models.ModelDiscoveryStatus.Mixed => ModelDiscoveryStatus.Mixed,
        _ => ModelDiscoveryStatus.Unknown,
    };

    /// <summary>
    /// Ensures that the profile's active or selected model exists in the inventory.
    /// Performs backward-compatible migration for existing profiles that lack an explicit inventory,
    /// while preserving an explicit user-disabled state for an existing item.
    /// </summary>
    public void EnsureSelectedModelMigrated(
        string? selectedModel,
        string? displayName = null,
        long? contextWindow = null,
        CodexModelOverrides? overrides = null)
    {
        if (string.IsNullOrWhiteSpace(selectedModel)) return;

        // Empty values can exist in older inventories even when the profile's
        // selected model is still valid. Treat blank as missing so the profile
        // default is restored without normalizing the exact model slug.
        if (string.IsNullOrWhiteSpace(SelectedModel))
        {
            SelectedModel = selectedModel;
        }

        var existing = Models.FirstOrDefault(m => string.Equals(m.Slug, selectedModel, StringComparison.Ordinal));
        if (existing == null)
        {
            Models.Insert(0, new ApiProviderModelItem
            {
                Slug = selectedModel,
                DisplayName = !string.IsNullOrWhiteSpace(displayName)
                    ? displayName
                    : ModelDisplayName.FromSlug(selectedModel),
                Enabled = true,
                DiscoverySource = ModelDiscoverySource.Manual,
                Availability = ModelAvailability.Reported,
                ContextWindow = contextWindow,
                UserOverrides = overrides != null ? new CodexModelOverrides
                {
                    ContextWindowTokens = overrides.ContextWindowTokens,
                    ReasoningEffort = overrides.ReasoningEffort,
                    Verbosity = overrides.Verbosity,
                } : null,
                LastSeenAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            if (existing.ContextWindow == null && contextWindow.HasValue)
            {
                existing.ContextWindow = contextWindow;
            }
            if (existing.UserOverrides == null && overrides != null)
            {
                existing.UserOverrides = new CodexModelOverrides
                {
                    ContextWindowTokens = overrides.ContextWindowTokens,
                    ReasoningEffort = overrides.ReasoningEffort,
                    Verbosity = overrides.Verbosity,
                };
            }
        }
    }

    /// <summary>
    /// Computes a deterministic 16-character SHA-256 fingerprint representing the current set of enabled models
    /// and their effective overrides.
    /// </summary>
    public string ComputeInventoryHash()
    {
        var sorted = Models
            .Where(m => m.Enabled)
            .OrderBy(m => m.Slug, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sorted.Count == 0 && !string.IsNullOrWhiteSpace(SelectedModel))
        {
            var bytes = Encoding.UTF8.GetBytes(SelectedModel.Trim().ToLowerInvariant());
            return Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        }

        var sb = new StringBuilder();
        foreach (var m in sorted)
        {
            sb.Append(m.Slug).Append('|')
              .Append(m.DisplayName ?? string.Empty).Append('|')
              .Append(m.ContextWindow?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|')
              .Append(m.UserOverrides?.ContextWindowTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|')
              .Append(m.UserOverrides?.ReasoningEffort.ToString() ?? string.Empty).Append('|')
              .Append(m.UserOverrides?.Verbosity.ToString() ?? string.Empty).Append('|')
              .Append(m.Capabilities?.Vision.State.ToString() ?? string.Empty).Append(';');
        }

        var raw = sb.ToString();
        if (string.IsNullOrEmpty(raw))
        {
            return "empty";
        }

        var hashBytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToHexString(SHA256.HashData(hashBytes))[..16].ToLowerInvariant();
    }
}
