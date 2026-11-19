using System.Text.Json;
using CodexSwitcher.Core.Providers.Catalog;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>Compatibility adapter for the provider kit's deterministic safe request planner.</summary>
public sealed class ProviderProbePlanner : IProviderProbePlanner
{
    private static readonly JsonSerializerOptions AdapterJson = new() { PropertyNameCaseInsensitive = true };
    private readonly Llm.ProviderKit.Inspection.ProviderProbePlanner _planner = new();

    public (bool Valid, ProviderProbePlan? Plan, string? Error) PlanProbe(
        ProviderDescriptor descriptor,
        ProviderCapabilityRecipe recipe,
        string baseUrl,
        string capabilityType)
    {
        var result = _planner.Plan(ToProviderKit(descriptor), ToProviderKit(recipe), baseUrl, capabilityType);
        return (result.IsValid, result.Plan is null ? null : FromProviderKit(result.Plan), result.Error);
    }

    public (bool Valid, ProviderProbePlan? Plan, ProviderDescriptor? SyntheticDescriptor, string? Error) PlanGenericUnknownModelsProbe(
        string rawBaseUrl)
    {
        var result = _planner.PlanUnknownModels(rawBaseUrl);
        return (result.IsValid,
            result.Plan is null ? null : FromProviderKit(result.Plan),
            result.SyntheticDescriptor is null ? null : FromProviderKit(result.SyntheticDescriptor),
            result.Error);
    }

    public string JoinBaseUrlAndPath(string baseUrl, string? path, string? strategy = null) =>
        _planner.JoinBaseUrlAndPath(baseUrl, path, strategy);

    private static Llm.ProviderKit.Catalog.ProviderDescriptor ToProviderKit(ProviderDescriptor descriptor) =>
        JsonSerializer.Deserialize<Llm.ProviderKit.Catalog.ProviderDescriptor>(
            JsonSerializer.SerializeToUtf8Bytes(descriptor, AdapterJson), AdapterJson)
        ?? throw new InvalidOperationException("Provider descriptor could not be mapped to ProviderKit.");

    private static Llm.ProviderKit.Catalog.ProviderCapabilityRecipe ToProviderKit(ProviderCapabilityRecipe recipe) =>
        JsonSerializer.Deserialize<Llm.ProviderKit.Catalog.ProviderCapabilityRecipe>(
            JsonSerializer.SerializeToUtf8Bytes(recipe, AdapterJson), AdapterJson)
        ?? throw new InvalidOperationException("Provider recipe could not be mapped to ProviderKit.");

    private static ProviderProbePlan FromProviderKit(Llm.ProviderKit.Inspection.ProviderProbePlan plan) =>
        new(plan.TargetUri, plan.Method, plan.AuthScheme, plan.Headers, plan.TrustedHosts, plan.Capability,
            plan.ModelsPointer, plan.ResponseMapping is null ? null : FromProviderKit(plan.ResponseMapping));

    private static ProviderDescriptor FromProviderKit(Llm.ProviderKit.Catalog.ProviderDescriptor descriptor) =>
        JsonSerializer.Deserialize<ProviderDescriptor>(JsonSerializer.SerializeToUtf8Bytes(descriptor, AdapterJson), AdapterJson)
        ?? throw new InvalidOperationException("Provider descriptor could not be mapped from ProviderKit.");

    private static RecipeResponseMapping FromProviderKit(Llm.ProviderKit.Catalog.RecipeResponseMapping mapping) =>
        JsonSerializer.Deserialize<RecipeResponseMapping>(JsonSerializer.SerializeToUtf8Bytes(mapping, AdapterJson), AdapterJson)
        ?? throw new InvalidOperationException("Provider response mapping could not be mapped from ProviderKit.");
}
