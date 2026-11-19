using System.Text.Json;
using CodexSwitcher.Core.Providers.Catalog;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>Compatibility adapter for ProviderKit's exact-ID and declarative fact mappers.</summary>
public sealed class ProviderProbeResponseMapper : IProviderProbeResponseMapper
{
    private static readonly JsonSerializerOptions AdapterJson = new() { PropertyNameCaseInsensitive = true };
    private readonly Llm.ProviderKit.Inspection.ProviderProbeResponseMapper _mapper = new();

    public (bool Success, List<string>? Models, string? Error) MapModelsResponse(ProbeHttpResponse response, string? jsonPointer)
    {
        var result = _mapper.MapModels(ToProviderKit(response), jsonPointer);
        return (result.Success, result.ModelIds, result.Error);
    }

    public void MapBalanceAndUsageResponse(ProbeHttpResponse response, RecipeResponseMapping? mapping,
        ref decimal? balance, ref decimal? usedCredits, ref decimal? creditLimit,
        ref decimal? remainingCredits, ref string? currency)
    {
        var responses = new Dictionary<string, Llm.ProviderKit.Inspection.ProbeHttpResponse>(StringComparer.Ordinal);
        var mappedResponse = ToProviderKit(response);
        responses["response"] = mappedResponse;
        if (mapping is not null)
        {
            foreach (var requestId in new[] { mapping.Balance?.RequestId, mapping.Used?.RequestId,
                         mapping.Limit?.RequestId, mapping.Remaining?.RequestId, mapping.Currency?.RequestId }
                     .Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>())
            {
                responses.TryAdd(requestId, mappedResponse);
            }
        }
        Apply(_mapper.MapBilling(responses, mapping is null ? null : ToProviderKit(mapping)),
            ref balance, ref usedCredits, ref creditLimit, ref remainingCredits, ref currency);
    }

    public void MapBalanceAndUsageResponses(IReadOnlyDictionary<string, ProbeHttpResponse> responses,
        RecipeResponseMapping? mapping, ref decimal? balance, ref decimal? usedCredits,
        ref decimal? creditLimit, ref decimal? remainingCredits, ref string? currency)
    {
        var mappedResponses = responses.ToDictionary(item => item.Key, item => ToProviderKit(item.Value), StringComparer.Ordinal);
        Apply(_mapper.MapBilling(mappedResponses, mapping is null ? null : ToProviderKit(mapping)),
            ref balance, ref usedCredits, ref creditLimit, ref remainingCredits, ref currency);
    }

    private static Llm.ProviderKit.Inspection.ProbeHttpResponse ToProviderKit(ProbeHttpResponse response) =>
        new(response.Success, response.Json, response.StatusCode, response.Error);

    private static Llm.ProviderKit.Catalog.RecipeResponseMapping ToProviderKit(RecipeResponseMapping mapping) =>
        JsonSerializer.Deserialize<Llm.ProviderKit.Catalog.RecipeResponseMapping>(
            JsonSerializer.SerializeToUtf8Bytes(mapping, AdapterJson), AdapterJson)
        ?? throw new InvalidOperationException("Provider response mapping could not be mapped to ProviderKit.");

    private static void Apply(Llm.ProviderKit.Inspection.ProviderBillingFacts facts,
        ref decimal? balance, ref decimal? usedCredits, ref decimal? creditLimit,
        ref decimal? remainingCredits, ref string? currency)
    {
        balance ??= facts.Balance;
        usedCredits ??= facts.Used;
        creditLimit ??= facts.Limit;
        remainingCredits ??= facts.Remaining;
        currency ??= facts.Currency;
    }
}
