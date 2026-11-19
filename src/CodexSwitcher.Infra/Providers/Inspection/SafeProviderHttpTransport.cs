using System.Text.Json;
using CodexSwitcher.Core.Providers.Catalog;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>Compatibility adapter over ProviderKit's bounded, redirect-safe HTTP transport.</summary>
public sealed class SafeProviderHttpTransport : ISafeProviderHttpTransport, IDisposable
{
    private static readonly JsonSerializerOptions AdapterJson = new() { PropertyNameCaseInsensitive = true };
    private readonly Llm.ProviderKit.Inspection.SafeProviderHttpTransport _transport;

    public SafeProviderHttpTransport(HttpClient? httpClient = null) =>
        _transport = new Llm.ProviderKit.Inspection.SafeProviderHttpTransport(httpClient);

    public async Task<ProbeHttpResponse> SendProbeAsync(ProviderProbePlan plan, ProviderDescriptor descriptor,
        string? apiKey, CancellationToken ct = default)
    {
        var mappedPlan = new Llm.ProviderKit.Inspection.ProviderProbePlan(
            plan.TargetUri, plan.Method, plan.AuthScheme, plan.Headers, plan.TrustedHosts, plan.CapabilityType,
            plan.ModelsPointer, plan.ResponseMapping is null ? null : ToProviderKit(plan.ResponseMapping));
        var mappedDescriptor = JsonSerializer.Deserialize<Llm.ProviderKit.Catalog.ProviderDescriptor>(
            JsonSerializer.SerializeToUtf8Bytes(descriptor, AdapterJson), AdapterJson)
            ?? throw new InvalidOperationException("Provider descriptor could not be mapped to ProviderKit.");
        var credential = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : Llm.ProviderKit.Inspection.ProviderCredential.FromSecret(apiKey);
        var response = await _transport.SendAsync(mappedPlan, mappedDescriptor, credential, ct).ConfigureAwait(false);
        return new ProbeHttpResponse(response.Success, response.Json, response.StatusCode, response.Error);
    }

    public void Dispose() => _transport.Dispose();

    private static Llm.ProviderKit.Catalog.RecipeResponseMapping ToProviderKit(RecipeResponseMapping mapping) =>
        JsonSerializer.Deserialize<Llm.ProviderKit.Catalog.RecipeResponseMapping>(
            JsonSerializer.SerializeToUtf8Bytes(mapping, AdapterJson), AdapterJson)
        ?? throw new InvalidOperationException("Provider response mapping could not be mapped to ProviderKit.");
}
