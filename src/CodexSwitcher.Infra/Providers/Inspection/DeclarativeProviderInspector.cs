using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>
/// Safe, non-Turing-complete declarative inspection facade for AI model providers.
/// Coordinates request planning (<see cref="IProviderProbePlanner"/>), hardened HTTP transport
/// (<see cref="ISafeProviderHttpTransport"/>), and fact extraction (<see cref="IProviderProbeResponseMapper"/>).
/// Strictly enforces GET/HEAD-only, exact trusted-host validation, redirect credential stripping,
/// response size capping, and normalized snapshot extraction.
/// </summary>
public sealed class DeclarativeProviderInspector : IDeclarativeProviderInspector, IDisposable
{
    private const int MaxBillingRequestsPerRecipe = 8;
    private const int MaxGenericBillingStrategies = 8;
    private readonly IProviderProbePlanner _planner;
    private readonly ISafeProviderHttpTransport _transport;
    private readonly IProviderProbeResponseMapper _mapper;
    private readonly IReadOnlyList<IGenericProviderBillingStrategy> _genericBillingStrategies;

    /// <summary>
    /// Decomposed constructor injecting dedicated planner, transport, and mapper.
    /// </summary>
    public DeclarativeProviderInspector(
        IProviderProbePlanner planner,
        ISafeProviderHttpTransport transport,
        IProviderProbeResponseMapper mapper,
        IEnumerable<IGenericProviderBillingStrategy>? genericBillingStrategies = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _genericBillingStrategies = (genericBillingStrategies ?? [])
            .Where(strategy => strategy is not null)
            .OrderBy(strategy => strategy.Order)
            .ThenBy(strategy => strategy.Id, StringComparer.Ordinal)
            .Take(MaxGenericBillingStrategies)
            .ToArray();
    }

    /// <summary>
    /// Backward-compatible constructor for existing tests and composition roots.
    /// </summary>
    public DeclarativeProviderInspector(HttpClient? httpClient = null)
        : this(
            new ProviderProbePlanner(),
            new SafeProviderHttpTransport(httpClient),
            new ProviderProbeResponseMapper())
    {
    }

    /// <summary>
    /// Inspects a provider using its catalog descriptor and returns a normalized ApiProviderSnapshot.
    /// </summary>
    public async Task<ApiProviderSnapshot> InspectAsync(
        ProviderDescriptor descriptor,
        string activeBaseUrl,
        string? apiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(activeBaseUrl))
        {
            throw new ArgumentException("Active base URL cannot be empty.", nameof(activeBaseUrl));
        }

        var capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["models"] = descriptor.Capabilities?.Models?.Status ?? CapabilityStatus.Unknown,
            ["balance"] = descriptor.Capabilities?.Balance?.Status ?? CapabilityStatus.Unknown,
            ["usage"] = descriptor.Capabilities?.Usage?.Status ?? CapabilityStatus.Unknown,
        };

        var modelsList = new List<string>();
        var responseCache = new Dictionary<string, ProbeHttpResponse>(StringComparer.Ordinal);

        async Task<ProbeHttpResponse> SendOnceAsync(ProviderProbePlan plan)
        {
            var cacheKey = CreateRequestCacheKey(plan);
            if (!responseCache.TryGetValue(cacheKey, out var response))
            {
                response = await _transport.SendProbeAsync(plan, descriptor, apiKey, ct).ConfigureAwait(false);
                responseCache[cacheKey] = response;
            }

            return response;
        }

        decimal? balance = null;
        string? currency = null;
        decimal? usedCredits = null;
        decimal? creditLimit = null;
        decimal? remainingCredits = null;
        string? overallError = null;
        var connectionStatus = HealthStatus.Unknown;

        // 1. Inspect Models if supported
        var modelsRecipe = descriptor.Capabilities?.Models;
        if (modelsRecipe != null && modelsRecipe.Status == CapabilityStatus.Supported)
        {
            var (valid, plan, planError) = _planner.PlanProbe(descriptor, modelsRecipe, activeBaseUrl, "models");
            if (!valid || plan == null)
            {
                connectionStatus = HealthStatus.Error;
                overallError = planError;
            }
            else
            {
                var response = await SendOnceAsync(plan).ConfigureAwait(false);
                var (success, models, mapError) = _mapper.MapModelsResponse(response, plan.ModelsPointer);
                if (success)
                {
                    connectionStatus = HealthStatus.Valid;
                    if (models != null)
                    {
                        modelsList.AddRange(models);
                    }
                }
                else
                {
                    connectionStatus = HealthStatus.Error;
                    overallError = mapError ?? response.Error;
                }
            }
        }

        // 2. Inspect Balance if supported. All plans are validated before any
        // request runs; a malformed/untrusted later request cannot leave a
        // partially executed recipe.
        var balanceRecipe = descriptor.Capabilities?.Balance;
        if (balanceRecipe != null && balanceRecipe.Status == CapabilityStatus.Supported)
        {
            var execution = await ExecuteBillingRecipeAsync(
                descriptor,
                activeBaseUrl,
                balanceRecipe,
                "balance",
                SendOnceAsync,
                ct).ConfigureAwait(false);
            overallError ??= execution.Error;
            if (execution.Valid)
            {
                if (execution.Responses.Values.Any(response => response.Success) && connectionStatus == HealthStatus.Unknown)
                {
                    connectionStatus = HealthStatus.Valid;
                }

                _mapper.MapBalanceAndUsageResponses(
                    execution.Responses,
                    balanceRecipe.Outputs ?? balanceRecipe.Response,
                    ref balance,
                    ref usedCredits,
                    ref creditLimit,
                    ref remainingCredits,
                    ref currency);
            }
        }

        // 3. Inspect Usage if supported and separate from balance
        var usageRecipe = descriptor.Capabilities?.Usage;
        if (usageRecipe != null && usageRecipe.Status == CapabilityStatus.Supported)
        {
            var execution = await ExecuteBillingRecipeAsync(
                descriptor,
                activeBaseUrl,
                usageRecipe,
                "usage",
                SendOnceAsync,
                ct).ConfigureAwait(false);
            overallError ??= execution.Error;
            if (execution.Valid)
            {
                _mapper.MapBalanceAndUsageResponses(
                    execution.Responses,
                    usageRecipe.Outputs ?? usageRecipe.Response,
                    ref balance,
                    ref usedCredits,
                    ref creditLimit,
                    ref remainingCredits,
                    ref currency);
            }
        }

        // Some documented key-metadata APIs report a hard currency limit and
        // usage but omit an explicit remaining field. Derive it only when the
        // signed recipe also establishes the unit/currency.
        if (!string.IsNullOrWhiteSpace(currency) && remainingCredits is null &&
            creditLimit.HasValue && usedCredits.HasValue)
        {
            remainingCredits = Math.Max(0m, creditLimit.Value - usedCredits.Value);
        }

        balance ??= remainingCredits;

        if (connectionStatus == HealthStatus.Unknown)
        {
            connectionStatus = overallError == null ? HealthStatus.Valid : HealthStatus.Unknown;
        }

        return new ApiProviderSnapshot
        {
            ConnectionStatus = connectionStatus,
            Models = modelsList,
            Balance = balance,
            Currency = currency,
            UsedCredits = usedCredits,
            RemainingCredits = remainingCredits,
            CreditLimit = creditLimit,
            Usage = usedCredits,
            Capabilities = capabilities,
            LastCheckedAt = DateTimeOffset.UtcNow,
            Error = overallError,
        };
    }

    private async Task<BillingRecipeExecutionResult> ExecuteBillingRecipeAsync(
        ProviderDescriptor descriptor,
        string activeBaseUrl,
        ProviderCapabilityRecipe recipe,
        string capability,
        Func<ProviderProbePlan, Task<ProbeHttpResponse>> sendOnceAsync,
        CancellationToken ct)
    {
        if (recipe.Requests is { Count: 0 })
        {
            return BillingRecipeExecutionResult.Invalid("Billing recipe declares an empty request list.");
        }

        var requests = recipe.Requests is { Count: > 0 }
            ? recipe.Requests
            : recipe.Request is null ? [] : [recipe.Request];

        if (requests.Count == 0)
        {
            return BillingRecipeExecutionResult.Invalid("Supported billing recipe is missing a read-only request.");
        }

        if (requests.Count > MaxBillingRequestsPerRecipe)
        {
            return BillingRecipeExecutionResult.Invalid(
                $"Billing recipe exceeds the {MaxBillingRequestsPerRecipe}-request limit.");
        }

        var planned = new List<(string Id, ProviderProbePlan Plan)>(requests.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var id = request.Id?.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                if (requests.Count > 1)
                {
                    return BillingRecipeExecutionResult.Invalid(
                        "Every request in a multi-request billing recipe must have an id.");
                }

                id = "default";
            }

            if (!IsSafeRequestId(id) || !ids.Add(id))
            {
                return BillingRecipeExecutionResult.Invalid("Billing recipe request ids must be unique safe identifiers.");
            }

            var singleRequestRecipe = new ProviderCapabilityRecipe
            {
                Status = recipe.Status,
                Strategy = recipe.Strategy,
                Request = request,
                Response = recipe.Outputs ?? recipe.Response,
            };
            var (valid, plan, error) = _planner.PlanProbe(descriptor, singleRequestRecipe, activeBaseUrl, capability);
            if (!valid || plan is null)
            {
                return BillingRecipeExecutionResult.Invalid(
                    error ?? $"Billing recipe request '{id}' could not be planned.");
            }

            planned.Add((id, plan));
        }

        // Plan every request first, then execute them sequentially through the
        // same bounded, redirect-safe transport used by single-request recipes.
        var responses = new Dictionary<string, ProbeHttpResponse>(StringComparer.Ordinal);
        string? firstError = null;
        foreach (var (id, plan) in planned)
        {
            ct.ThrowIfCancellationRequested();
            var response = await sendOnceAsync(plan).ConfigureAwait(false);
            responses[id] = response;
            if (!response.Success)
            {
                firstError ??= response.Error ?? "Billing request failed.";
            }
        }

        return new BillingRecipeExecutionResult(true, responses, firstError);
    }

    private static bool IsSafeRequestId(string id)
    {
        if (id.Length is < 1 or > 64 || !char.IsAsciiLetter(id[0]))
        {
            return false;
        }

        return id.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private sealed record BillingRecipeExecutionResult(
        bool Valid,
        IReadOnlyDictionary<string, ProbeHttpResponse> Responses,
        string? Error)
    {
        public static BillingRecipeExecutionResult Invalid(string error) =>
            new(false, new Dictionary<string, ProbeHttpResponse>(StringComparer.Ordinal), error);
    }

    /// <inheritdoc />
    public async Task<ApiProviderSnapshot> DiscoverModelsAsync(
        ProviderDescriptor descriptor,
        string activeBaseUrl,
        string? apiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(activeBaseUrl))
        {
            throw new ArgumentException("Active base URL cannot be empty.", nameof(activeBaseUrl));
        }

        var modelsRecipe = descriptor.Capabilities?.Models;
        if (modelsRecipe is null || modelsRecipe.Status != CapabilityStatus.Supported)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Unknown,
                Capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    ["models"] = modelsRecipe?.Status ?? CapabilityStatus.Unknown,
                },
                Error = "This provider does not declare a supported GET /models strategy.",
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var (valid, plan, planError) = _planner.PlanProbe(descriptor, modelsRecipe, activeBaseUrl, "models");
        if (!valid || plan is null)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Error,
                Capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    ["models"] = modelsRecipe.Status,
                },
                Error = planError,
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var response = await _transport.SendProbeAsync(plan, descriptor, apiKey, ct).ConfigureAwait(false);
        var (success, models, mapError) = _mapper.MapModelsResponse(response, plan.ModelsPointer);
        return new ApiProviderSnapshot
        {
            ConnectionStatus = success ? HealthStatus.Valid : HealthStatus.Error,
            Models = models ?? [],
            Capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["models"] = success ? CapabilityStatus.Supported : CapabilityStatus.Unknown,
            },
            Error = mapError ?? response.Error,
            LastCheckedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Inspects an unknown generic OpenAI-compatible provider using safe default endpoints.
    /// strictly never guesses balance/credits paths.
    /// </summary>
    public async Task<ApiProviderSnapshot> InspectGenericUnknownAsync(
        string rawBaseUrl,
        string? apiKey,
        CancellationToken ct = default)
    {
        var (valid, plan, syntheticDescriptor, planError) = _planner.PlanGenericUnknownModelsProbe(rawBaseUrl);
        if (!valid || plan == null || syntheticDescriptor == null)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Unknown,
                Error = planError ?? "Invalid base URL format.",
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var responseCache = new Dictionary<string, ProbeHttpResponse>(StringComparer.Ordinal);
        async Task<ProbeHttpResponse> SendOnceAsync(ProviderProbePlan requestPlan)
        {
            var cacheKey = CreateRequestCacheKey(requestPlan);
            if (!responseCache.TryGetValue(cacheKey, out var cached))
            {
                cached = await _transport.SendProbeAsync(requestPlan, syntheticDescriptor, apiKey, ct).ConfigureAwait(false);
                responseCache[cacheKey] = cached;
            }

            return cached;
        }

        var response = await SendOnceAsync(plan).ConfigureAwait(false);
        var (success, models, mapError) = _mapper.MapModelsResponse(response, plan.ModelsPointer);

        var capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["models"] = success ? CapabilityStatus.Supported : CapabilityStatus.Unknown,
            ["balance"] = CapabilityStatus.Unknown,
            ["usage"] = CapabilityStatus.Unknown,
        };

        decimal? balance = null;
        decimal? usedCredits = null;
        decimal? creditLimit = null;
        decimal? remainingCredits = null;
        string? currency = null;
        string? billingError = null;
        var providerOrigin = new Uri(plan.TargetUri.GetLeftPart(UriPartial.Authority));

        foreach (var strategy in _genericBillingStrategies)
        {
            ProviderCapabilityRecipe? recipe;
            try
            {
                recipe = strategy.CreateRecipe(providerOrigin);
            }
            catch (Exception)
            {
                billingError ??= "A generic billing strategy could not create its read-only recipe.";
                continue;
            }

            if (recipe is null || recipe.Status != CapabilityStatus.Supported)
            {
                continue;
            }

            var outputMapping = recipe.Outputs ?? recipe.Response;
            if (outputMapping?.Balance is not null)
            {
                capabilities["balance"] = CapabilityStatus.Supported;
            }
            if (outputMapping?.Used is not null || outputMapping?.Limit is not null || outputMapping?.Remaining is not null)
            {
                capabilities["usage"] = CapabilityStatus.Supported;
            }

            var execution = await ExecuteBillingRecipeAsync(
                syntheticDescriptor,
                rawBaseUrl,
                recipe,
                "generic-billing",
                SendOnceAsync,
                ct).ConfigureAwait(false);
            if (execution.Valid)
            {
                _mapper.MapBalanceAndUsageResponses(
                    execution.Responses,
                    outputMapping,
                    ref balance,
                    ref usedCredits,
                    ref creditLimit,
                    ref remainingCredits,
                    ref currency);
            }

            if (balance.HasValue || usedCredits.HasValue || creditLimit.HasValue || remainingCredits.HasValue)
            {
                billingError = null;
                break;
            }

            billingError ??= execution.Error;

            // Do not send the same stored credential to another endpoint after
            // an explicit authorization rejection.
            if (execution.Error?.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) == true ||
                execution.Error?.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase) == true)
            {
                break;
            }
        }

        return new ApiProviderSnapshot
        {
            ConnectionStatus = success ? HealthStatus.Valid : HealthStatus.Unknown,
            Models = models ?? [],
            Balance = balance ?? remainingCredits,
            Currency = currency,
            UsedCredits = usedCredits,
            Usage = usedCredits,
            CreditLimit = creditLimit,
            RemainingCredits = remainingCredits,
            Capabilities = capabilities,
            LastCheckedAt = DateTimeOffset.UtcNow,
            Error = billingError ?? mapError ?? response.Error,
        };
    }

    /// <summary>
    /// Inspects models for an unknown provider without invoking billing fallback
    /// strategies. Used by explicit model-refresh/editor flows.
    /// </summary>
    public async Task<ApiProviderSnapshot> DiscoverGenericUnknownModelsAsync(
        string rawBaseUrl,
        string? apiKey,
        CancellationToken ct = default)
    {
        var (valid, plan, descriptor, planError) = _planner.PlanGenericUnknownModelsProbe(rawBaseUrl);
        if (!valid || plan is null || descriptor is null)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Unknown,
                Error = planError ?? "Invalid base URL format.",
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var response = await _transport.SendProbeAsync(plan, descriptor, apiKey, ct).ConfigureAwait(false);
        var (success, models, error) = _mapper.MapModelsResponse(response, plan.ModelsPointer);
        return new ApiProviderSnapshot
        {
            ConnectionStatus = success ? HealthStatus.Valid : HealthStatus.Unknown,
            Models = models ?? [],
            Capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["models"] = success ? CapabilityStatus.Supported : CapabilityStatus.Unknown,
                ["balance"] = CapabilityStatus.Unknown,
                ["usage"] = CapabilityStatus.Unknown,
            },
            LastCheckedAt = DateTimeOffset.UtcNow,
            Error = error ?? response.Error,
        };
    }

    /// <summary>
    /// Safely joins a route base URL and a request path without duplication (/v1 + /models != /v1/v1/models).
    /// Enforces OpenAI-compatible path normalization.
    /// </summary>
    public static string JoinBaseUrlAndPath(string baseUrl, string? path, string? strategy = null)
    {
        return new ProviderProbePlanner().JoinBaseUrlAndPath(baseUrl, path, strategy);
    }

    public void Dispose()
    {
        if (_transport is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static string CreateRequestCacheKey(ProviderProbePlan plan)
    {
        var headers = plan.Headers is null
            ? string.Empty
            : string.Join("\n", plan.Headers
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key.Trim().ToLowerInvariant()}:{pair.Value.Trim()}"));

        return $"{plan.Method.Trim().ToUpperInvariant()}\n{plan.TargetUri.AbsoluteUri}\n{plan.AuthScheme.Trim().ToLowerInvariant()}\n{headers}";
    }
}
