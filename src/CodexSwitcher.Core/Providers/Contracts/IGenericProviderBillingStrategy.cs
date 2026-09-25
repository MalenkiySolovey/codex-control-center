using CodexSwitcher.Core.Providers.Catalog;

namespace CodexSwitcher.Core.Providers.Contracts;

/// <summary>
/// Supplies a documented read-only billing recipe for an otherwise unknown
/// OpenAI-compatible provider. The inspector, planner, trusted-host checks and
/// safe transport still own execution; strategies never receive credentials or
/// raw responses.
/// </summary>
public interface IGenericProviderBillingStrategy
{
    /// <summary>Stable identifier used only for diagnostics and ordering.</summary>
    string Id { get; }

    /// <summary>Lower values are considered first.</summary>
    int Order { get; }

    /// <summary>
    /// Returns a data-only recipe when this strategy has documented evidence
    /// for the supplied origin, or null when it does not apply.
    /// </summary>
    ProviderCapabilityRecipe? CreateRecipe(Uri providerOrigin);
}
