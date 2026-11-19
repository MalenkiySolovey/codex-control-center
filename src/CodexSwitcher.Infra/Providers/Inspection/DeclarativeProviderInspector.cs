using System.Net.Http;
using System.Text.Json;
using ChatGpt.Accounts;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>
/// Product adapter that keeps credential lookup and Control Center snapshot types at the boundary.
/// Probe planning, HTTP transport, response mapping, and billing recipe execution live in ProviderKit.
/// </summary>
public sealed class DeclarativeProviderInspector : IDeclarativeProviderInspector, IDisposable
{
    private static readonly JsonSerializerOptions AdapterJson = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient? _httpClient;
    private readonly IGenericProviderBillingStrategy[] _billingStrategies;

    public DeclarativeProviderInspector(HttpClient? httpClient = null,
        IEnumerable<IGenericProviderBillingStrategy>? genericBillingStrategies = null)
    {
        _httpClient = httpClient;
        _billingStrategies = (genericBillingStrategies ?? [])
            .Where(strategy => strategy is not null)
            .OrderBy(strategy => strategy.Order)
            .ThenBy(strategy => strategy.Id, StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    public async Task<ApiProviderSnapshot> DiscoverModelsAsync(ProviderDescriptor descriptor, string activeBaseUrl,
        string? apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        using var inspector = CreateInspector(apiKey, descriptor.Id, descriptor.Id, includeBillingStrategies: false);
        var result = await inspector.DiscoverModelsAsync(ToProviderKit(descriptor), descriptor.Id, activeBaseUrl, ct)
            .ConfigureAwait(false);
        return ToSnapshot(result);
    }

    public async Task<ApiProviderSnapshot> InspectAsync(ProviderDescriptor descriptor, string activeBaseUrl,
        string? apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        using var inspector = CreateInspector(apiKey, descriptor.Id, descriptor.Id, includeBillingStrategies: true);
        return ToSnapshot(await inspector.InspectAsync(ToProviderKit(descriptor), descriptor.Id, activeBaseUrl, ct).ConfigureAwait(false));
    }

    public async Task<ApiProviderSnapshot> InspectGenericUnknownAsync(string rawBaseUrl, string? apiKey,
        CancellationToken ct = default)
    {
        using var inspector = CreateInspector(apiKey, "generic-unknown", "generic-unknown", includeBillingStrategies: true);
        return ToSnapshot(await inspector.InspectUnknownAsync("generic-unknown", rawBaseUrl,
            "generic-unknown", ct).ConfigureAwait(false));
    }

    public async Task<ApiProviderSnapshot> DiscoverGenericUnknownModelsAsync(string rawBaseUrl, string? apiKey,
        CancellationToken ct = default)
    {
        using var inspector = CreateInspector(apiKey, "generic-unknown", "generic-unknown", includeBillingStrategies: false);
        return ToSnapshot(await inspector.InspectUnknownAsync("generic-unknown", rawBaseUrl,
            "generic-unknown", ct).ConfigureAwait(false));
    }

    public static string JoinBaseUrlAndPath(string baseUrl, string? path, string? strategy = null) =>
        new Llm.ProviderKit.Inspection.ProviderProbePlanner().JoinBaseUrlAndPath(baseUrl, path, strategy);

    public void Dispose()
    {
        // The optional HttpClient is caller-owned; per-operation ProviderKit transports are disposed inline.
    }

    private Llm.ProviderKit.Inspection.ProviderInspectionService CreateInspector(string? apiKey,
        string providerId, string endpointId, bool includeBillingStrategies)
    {
        Llm.ProviderKit.Inspection.IProviderCredentialSource? credentials = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new Llm.ProviderKit.Inspection.InMemoryProviderCredentialSource([(providerId, endpointId, apiKey)]);
        var strategies = includeBillingStrategies
            ? _billingStrategies.Select(strategy => new BillingStrategyAdapter(strategy)).ToArray()
            : [];
        var transport = new Llm.ProviderKit.Inspection.SafeProviderHttpTransport(_httpClient);
        return new Llm.ProviderKit.Inspection.ProviderInspectionService(
            new Llm.ProviderKit.Inspection.ProviderProbePlanner(), transport,
            new Llm.ProviderKit.Inspection.ProviderProbeResponseMapper(), credentials, strategies);
    }

    private static Llm.ProviderKit.Catalog.ProviderDescriptor ToProviderKit(ProviderDescriptor descriptor) =>
        JsonSerializer.Deserialize<Llm.ProviderKit.Catalog.ProviderDescriptor>(
            JsonSerializer.SerializeToUtf8Bytes(descriptor, AdapterJson), AdapterJson)
        ?? throw new InvalidOperationException("Provider descriptor could not be mapped to ProviderKit.");

    private static ApiProviderSnapshot ToSnapshot(Llm.ProviderKit.Inspection.ProviderInspectionResult result)
    {
        var capabilities = result.Capabilities.ToDictionary(
            item => item.Key,
            item => Enum.TryParse<CapabilityStatus>(item.Value.ToString(), out var status) ? status : CapabilityStatus.Unknown,
            StringComparer.OrdinalIgnoreCase);
        return new ApiProviderSnapshot
        {
            ConnectionStatus = result.Succeeded ? HealthStatus.Valid : HealthStatus.Unknown,
            Models = result.ModelIds,
            Balance = result.Billing.Balance ?? result.Billing.Remaining,
            Currency = result.Billing.Currency,
            UsedCredits = result.Billing.Used,
            Usage = result.Billing.Used,
            CreditLimit = result.Billing.Limit,
            RemainingCredits = result.Billing.Remaining,
            Capabilities = capabilities,
            LastCheckedAt = result.ObservedAt,
            Error = result.Error,
        };
    }

    private sealed class BillingStrategyAdapter(IGenericProviderBillingStrategy strategy)
        : Llm.ProviderKit.Inspection.IGenericProviderBillingStrategy
    {
        public string Id => strategy.Id;
        public int Order => strategy.Order;

        public Llm.ProviderKit.Catalog.ProviderCapabilityRecipe? CreateRecipe(Uri providerOrigin)
        {
            var recipe = strategy.CreateRecipe(providerOrigin);
            return recipe is null
                ? null
                : JsonSerializer.Deserialize<Llm.ProviderKit.Catalog.ProviderCapabilityRecipe>(
                    JsonSerializer.SerializeToUtf8Bytes(recipe, AdapterJson), AdapterJson);
        }
    }
}
