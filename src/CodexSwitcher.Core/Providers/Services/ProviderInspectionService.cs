using System.Collections.Concurrent;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using Llm.ProviderKit.Inspection;

namespace CodexSwitcher.Core.Providers.Services;

/// <summary>
/// Secure domain service for declarative AI provider inspection.
/// Enforces that stored plaintext credentials never escape into presentation view models.
/// </summary>
public sealed class ProviderInspectionService : IProviderInspectionService
{
    private readonly IApiProviderStore _apiProviderStore;
    private readonly IApiKeySecretStore _secretStore;
    private readonly IProviderCatalogService _catalogService;
    private readonly IDeclarativeProviderInspector _inspector;
    private readonly IProviderModelCache? _modelCache;
    private readonly ConcurrentDictionary<Guid, long> _billingGenerations = new();
    private readonly ConcurrentDictionary<Guid, object> _billingCommitGates = new();

    public ProviderInspectionService(
        IApiProviderStore apiProviderStore,
        IApiKeySecretStore secretStore,
        IProviderCatalogService catalogService,
        IDeclarativeProviderInspector inspector,
        IProviderModelCache? modelCache = null)
    {
        _apiProviderStore = apiProviderStore ?? throw new ArgumentNullException(nameof(apiProviderStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _modelCache = modelCache;
    }

    /// <inheritdoc />
    public async Task<ApiProviderSnapshot> InspectAsync(Guid providerProfileId, CancellationToken cancellationToken = default)
    {
        var profile = _apiProviderStore.GetById(providerProfileId);
        if (profile == null)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Error,
                Error = "Provider profile not found.",
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var generation = _billingGenerations.AddOrUpdate(profile.Id, 1, static (_, current) => current + 1);

        // Endpoint-bound credentials are intentionally shared by model
        // profiles. Reading by profile ID caused card discovery to emit a
        // false 401 while the edit dialog used the correct endpoint ID.
        var secretOwnerId = profile.EndpointId ?? profile.Id;
        var credentialRevisionBefore = _secretStore.GetApiKeyRevision(secretOwnerId);
        string? apiKey = _secretStore.GetApiKey(secretOwnerId);
        var credentialRevision = _secretStore.GetApiKeyRevision(secretOwnerId);
        if (!string.Equals(credentialRevisionBefore, credentialRevision, StringComparison.Ordinal))
        {
            return CredentialChangedSnapshot();
        }
        var descriptor = !string.IsNullOrWhiteSpace(profile.CatalogProviderId)
            ? _catalogService.GetDescriptor(profile.CatalogProviderId)
            : null;

        var routeKey = !string.IsNullOrWhiteSpace(profile.SelectedRouteId)
            ? profile.SelectedRouteId
            : profile.BaseUrl;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var missingCredential = new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Unknown,
                LastCheckedAt = DateTimeOffset.UtcNow,
                Error = "No API key is stored for this provider.",
                MetricSnapshot = new ProviderMetricSnapshot
                {
                    ObservedAt = DateTimeOffset.UtcNow,
                    Availability = ProviderMetricAvailability.Unknown,
                    BalanceAvailability = ProviderMetricAvailability.Unknown,
                    UsageAvailability = ProviderMetricAvailability.Unknown,
                    Diagnostic = "No API key is stored for this provider.",
                },
            };
            PersistObservationIfCurrent(profile, secretOwnerId, credentialRevision, descriptor, missingCredential, generation);
            return missingCredential;
        }

        ApiProviderSnapshot snapshot;
        try
        {
            if (descriptor != null)
            {
                snapshot = await _inspector.InspectAsync(descriptor, profile.BaseUrl, apiKey, cancellationToken);
            }
            else
            {
                snapshot = await _inspector.InspectGenericUnknownAsync(profile.BaseUrl, apiKey, cancellationToken);
            }

            if (snapshot.Models.Count > 0 && _modelCache != null)
            {
                _modelCache.SetModels(profile.Id, routeKey, 1, snapshot.Models);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            snapshot = new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Error,
                Error = SanitizeMessage(ex.Message),
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        PersistObservationIfCurrent(profile, secretOwnerId, credentialRevision, descriptor, snapshot, generation);

        return snapshot;
    }

    private void PersistObservationIfCurrent(ApiProviderProfile profile, Guid credentialOwnerId,
        string? credentialRevision, ProviderDescriptor? descriptor, ApiProviderSnapshot observation, long generation)
    {
        var commitGate = _billingCommitGates.GetOrAdd(profile.Id, static _ => new object());
        lock (commitGate)
        {
            if (_billingGenerations.TryGetValue(profile.Id, out var latestGeneration) && latestGeneration == generation &&
                HasSameProfileIdentity(_apiProviderStore.GetById(profile.Id), profile) &&
                string.Equals(_secretStore.GetApiKeyRevision(credentialOwnerId), credentialRevision, StringComparison.Ordinal))
            {
                PersistBillingObservation(profile, credentialOwnerId, credentialRevision, descriptor, observation);
            }
        }
    }

    private void PersistBillingObservation(
        ApiProviderProfile profile,
        Guid credentialOwnerId,
        string? credentialRevision,
        ProviderDescriptor? descriptor,
        ApiProviderSnapshot observation)
    {
        var latestProfile = _apiProviderStore.GetById(profile.Id);
        if (!HasSameProfileIdentity(latestProfile, profile)) return;

        var metrics = observation.MetricSnapshot.Metrics ?? Array.Empty<ProviderMetric>();
        var hasTypedFact = metrics.Any(metric => (metric.Value.HasValue || metric.IsUnlimited) && metric.Availability is
            ProviderMetricAvailability.Live or ProviderMetricAvailability.Partial);
        var hasLegacyFact = observation.Balance.HasValue || observation.UsedCredits.HasValue ||
                            observation.Usage.HasValue || observation.RemainingCredits.HasValue ||
                            observation.CreditLimit.HasValue;
        var balanceAvailability = ResolveCategoryAvailability(observation, isUsage: false);
        var usageAvailability = ResolveCategoryAvailability(observation, isUsage: true);
        var availability = observation.MetricSnapshot.Availability;
        var allUnsupported = IsCapability(observation, "balance", CapabilityStatus.Unsupported) &&
                             IsCapability(observation, "usage", CapabilityStatus.Unsupported);
        var hasSupportedRecipe = IsCapability(observation, "balance", CapabilityStatus.Supported) ||
                                 IsCapability(observation, "usage", CapabilityStatus.Supported);
        var status = hasTypedFact
            ? ToBillingStatus(availability == ProviderMetricAvailability.Unknown ? ProviderMetricAvailability.Partial : availability)
            : allUnsupported
                ? ProviderBillingStatus.Unsupported
                : !hasSupportedRecipe && availability == ProviderMetricAvailability.Unknown
                    ? ProviderBillingStatus.Unknown
                    : ToBillingStatus(availability == ProviderMetricAvailability.Unknown
                        ? ClassifyFailedAvailability(observation.Error, hasLegacyFact)
                        : availability);
        var diagnostic = hasTypedFact
            ? SanitizeMessage(observation.MetricSnapshot.Diagnostic ?? observation.Error ?? string.Empty)
            : hasLegacyFact
                ? "Saved provider values lack typed accounting semantics; refresh to verify them."
                : BuildBillingDiagnostic(observation, allUnsupported, hasSupportedRecipe);

        var current = latestProfile!.LastBillingSnapshot;
        var canReuseCurrent = MatchesSnapshotIdentity(current, profile, credentialOwnerId, credentialRevision);
        var mergedMetrics = MergeMetrics(
            canReuseCurrent ? current!.Metrics ?? Array.Empty<ProviderMetric>() : Array.Empty<ProviderMetric>(),
            metrics,
            balanceAvailability,
            usageAvailability);
        var latestMetricAt = mergedMetrics.Where(metric => metric.Value.HasValue || metric.IsUnlimited)
            .Select(metric => (DateTimeOffset?)metric.ObservedAt).Max() ??
            (canReuseCurrent ? current!.ObservedAt : observation.LastCheckedAt);
        var typedBalance = SingleMetric(mergedMetrics, ProviderMetricKind.AvailableBalance);
        var typedUsed = SingleMetric(mergedMetrics, ProviderMetricKind.Spending, ProviderMetricKind.ProviderSpecificUsage,
            ProviderMetricKind.ProviderCredits, ProviderMetricKind.RequestCount, ProviderMetricKind.TokenCount);
        var typedLimit = SingleMetric(mergedMetrics, ProviderMetricKind.QuotaLimit, ProviderMetricKind.RequestAllowance,
            ProviderMetricKind.TokenAllowance);
        var typedRemaining = SingleMetric(mergedMetrics, ProviderMetricKind.QuotaRemaining);
        var typedFacts = mergedMetrics.Where(metric => metric.Value.HasValue).ToArray();
        var distinctUnits = typedFacts.Select(metric => metric.Unit).Distinct().ToArray();
        var currency = distinctUnits.Length == 1 && distinctUnits[0].Kind == ProviderMetricUnitKind.Currency
            ? distinctUnits[0].Code
            : null;
        var unit = distinctUnits.Length == 1 ? distinctUnits[0].Code ?? distinctUnits[0].Kind.ToString() : null;
        var periods = typedFacts.Select(metric => metric.Period).Distinct(StringComparer.Ordinal).ToArray();
        var resets = typedFacts.Select(metric => metric.ResetAt).Distinct().ToArray();

        var next = new ProviderBillingSnapshot
        {
            Metrics = mergedMetrics,
            Availability = availability,
            BalanceAvailability = balanceAvailability,
            UsageAvailability = usageAvailability,
            ProfileId = profile.Id,
            ProviderId = profile.CatalogProviderId,
            BaseUrl = profile.BaseUrl,
            RouteId = profile.SelectedRouteId,
            CredentialOwnerId = credentialOwnerId,
            CredentialRevision = credentialRevision,
            Balance = typedBalance?.Value ?? (metrics.Count == 0 ? observation.Balance : null),
            Used = typedUsed?.Value ?? (metrics.Count == 0 ? observation.UsedCredits ?? observation.Usage : null),
            Limit = typedLimit?.Value ?? (metrics.Count == 0 ? observation.CreditLimit : null),
            Remaining = typedRemaining?.Value ?? (metrics.Count == 0 ? observation.RemainingCredits : null),
            Currency = currency ?? (metrics.Count == 0 ? NormalizeFact(observation.Currency) : null),
            Unit = unit ?? (metrics.Count == 0 ? NormalizeFact(observation.Currency) : null),
            Period = periods.Length == 1 ? periods[0] : null,
            ResetAt = resets.Length == 1 ? resets[0] : null,
            Source = descriptor?.DisplayName,
            ObservedAt = latestMetricAt,
            LastAttemptAt = observation.LastCheckedAt,
            Status = status,
            Diagnostic = diagnostic,
        };

        if (hasLegacyFact && !hasTypedFact)
        {
            next = next with { Diagnostic = "Saved provider values lack typed accounting semantics; refresh to verify them." };
        }

        _apiProviderStore.TryUpdateBillingSnapshot(profile, next);
    }

    private static bool IsCapability(ApiProviderSnapshot snapshot, string name, CapabilityStatus status) =>
        snapshot.Capabilities.TryGetValue(name, out var actual) && actual == status;

    private static ProviderMetricAvailability ResolveCategoryAvailability(ApiProviderSnapshot snapshot, bool isUsage)
    {
        var availability = isUsage
            ? snapshot.MetricSnapshot.UsageAvailability
            : snapshot.MetricSnapshot.BalanceAvailability;
        if (availability != ProviderMetricAvailability.Unknown) return availability;

        var name = isUsage ? "usage" : "balance";
        if (IsCapability(snapshot, name, CapabilityStatus.Unsupported)) return ProviderMetricAvailability.Unsupported;
        var hasAnyMetric = snapshot.MetricSnapshot.Metrics.Count > 0;
        var hasCategoryMetric = snapshot.MetricSnapshot.Metrics.Any(metric => IsUsageMetric(metric.Kind) == isUsage);
        if (hasAnyMetric && !hasCategoryMetric) return ProviderMetricAvailability.Unknown;
        if (IsCapability(snapshot, name, CapabilityStatus.Supported))
        {
            if (hasCategoryMetric)
                return snapshot.MetricSnapshot.Availability;
            return ClassifyFailedAvailability(snapshot.Error, hasLegacyFact: false);
        }

        return ProviderMetricAvailability.Unknown;
    }

    private static ProviderMetricAvailability ClassifyFailedAvailability(string? error, bool hasLegacyFact)
    {
        if (error?.Contains("No API key", StringComparison.OrdinalIgnoreCase) == true)
            return ProviderMetricAvailability.Unknown;
        if (error?.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) == true ||
            error?.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase) == true)
            return ProviderMetricAvailability.Unauthorized;
        if (error?.Contains("malformed", StringComparison.OrdinalIgnoreCase) == true ||
            error?.Contains("invalid response", StringComparison.OrdinalIgnoreCase) == true)
            return ProviderMetricAvailability.InvalidResponse;
        if (!string.IsNullOrWhiteSpace(error)) return ProviderMetricAvailability.Unavailable;
        return hasLegacyFact ? ProviderMetricAvailability.Partial : ProviderMetricAvailability.Unknown;
    }

    private static ProviderBillingStatus ToBillingStatus(ProviderMetricAvailability availability) => availability switch
    {
        ProviderMetricAvailability.Live => ProviderBillingStatus.Live,
        ProviderMetricAvailability.Partial => ProviderBillingStatus.Partial,
        ProviderMetricAvailability.Cached => ProviderBillingStatus.Cached,
        ProviderMetricAvailability.Stale => ProviderBillingStatus.Stale,
        ProviderMetricAvailability.Unsupported => ProviderBillingStatus.Unsupported,
        ProviderMetricAvailability.Unavailable => ProviderBillingStatus.Unavailable,
        ProviderMetricAvailability.Unauthorized => ProviderBillingStatus.Unauthorized,
        ProviderMetricAvailability.InvalidResponse => ProviderBillingStatus.InvalidResponse,
        _ => ProviderBillingStatus.Unknown,
    };

    private static ProviderMetric[] MergeMetrics(
        IReadOnlyList<ProviderMetric> previous,
        IReadOnlyList<ProviderMetric> incoming,
        ProviderMetricAvailability balanceAvailability,
        ProviderMetricAvailability usageAvailability)
    {
        var oldById = previous.ToDictionary(metric => metric.Id, StringComparer.Ordinal);
        var incomingIds = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<ProviderMetric>();
        foreach (var metric in incoming)
        {
            incomingIds.Add(metric.Id);
            if (metric.Value.HasValue)
            {
                merged.Add(metric);
            }
            else if (oldById.TryGetValue(metric.Id, out var oldMetric) && AreSemanticsCompatible(oldMetric, metric) && oldMetric.Value.HasValue)
            {
                merged.Add(oldMetric with { Availability = ProviderMetricAvailability.Cached });
            }
            else
            {
                merged.Add(metric);
            }
        }

        foreach (var oldMetric in previous)
        {
            if (incomingIds.Contains(oldMetric.Id)) continue;
            var categoryAvailability = IsUsageMetric(oldMetric.Kind) ? usageAvailability : balanceAvailability;
            if (categoryAvailability is ProviderMetricAvailability.Live or ProviderMetricAvailability.Unsupported) continue;
            var conflicts = incoming.Any(newMetric => newMetric.Kind == oldMetric.Kind && !AreSemanticsCompatible(oldMetric, newMetric));
            if (!conflicts && oldMetric.Value.HasValue)
                merged.Add(oldMetric with { Availability = ProviderMetricAvailability.Cached });
        }

        return merged
            .GroupBy(metric => metric.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
    }

    private static bool AreSemanticsCompatible(ProviderMetric left, ProviderMetric right) =>
        left.Kind == right.Kind && left.Scope == right.Scope &&
        ProviderMetricNormalizer.UnitsEqual(left.Unit, right.Unit) &&
        string.Equals(left.Period, right.Period, StringComparison.Ordinal) &&
        left.PeriodSemantics == right.PeriodSemantics &&
        left.PeriodStartAt == right.PeriodStartAt && left.PeriodEndAt == right.PeriodEndAt &&
        left.ResetAt == right.ResetAt &&
        string.Equals(left.Source, right.Source, StringComparison.Ordinal);

    private static ProviderMetric? SingleMetric(IReadOnlyList<ProviderMetric> metrics, params ProviderMetricKind[] kinds)
    {
        var selected = metrics.Where(metric => metric.Value.HasValue && kinds.Contains(metric.Kind)).ToArray();
        return selected.Length == 1 ? selected[0] : null;
    }

    private static bool IsUsageMetric(ProviderMetricKind kind) => kind is
        ProviderMetricKind.Spending or ProviderMetricKind.QuotaLimit or ProviderMetricKind.QuotaRemaining or
        ProviderMetricKind.ProviderCredits or ProviderMetricKind.ProviderSpecificUsage or ProviderMetricKind.RequestCount or
        ProviderMetricKind.RequestAllowance or ProviderMetricKind.TokenCount or ProviderMetricKind.TokenAllowance;

    private static bool MatchesSnapshotIdentity(ProviderBillingSnapshot? snapshot, ApiProviderProfile profile,
        Guid credentialOwnerId, string? credentialRevision) => snapshot is not null &&
        snapshot.ProfileId == profile.Id && snapshot.CredentialOwnerId == credentialOwnerId &&
        string.Equals(snapshot.CredentialRevision, credentialRevision, StringComparison.Ordinal) &&
        string.Equals(snapshot.ProviderId, profile.CatalogProviderId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(snapshot.BaseUrl, profile.BaseUrl, StringComparison.Ordinal) &&
        string.Equals(snapshot.RouteId, profile.SelectedRouteId, StringComparison.OrdinalIgnoreCase);

    private static bool HasSameProfileIdentity(ApiProviderProfile? current, ApiProviderProfile expected) => current is not null &&
        current.Id == expected.Id && current.CreatedAt == expected.CreatedAt &&
        string.Equals(current.StableCodexProviderId, expected.StableCodexProviderId, StringComparison.Ordinal) &&
        current.EndpointId == expected.EndpointId &&
        string.Equals(current.CatalogProviderId, expected.CatalogProviderId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(current.BaseUrl, expected.BaseUrl, StringComparison.Ordinal) &&
        string.Equals(current.SelectedRouteId, expected.SelectedRouteId, StringComparison.OrdinalIgnoreCase);

    private static ApiProviderSnapshot CredentialChangedSnapshot() => new()
    {
        ConnectionStatus = HealthStatus.Unknown,
        LastCheckedAt = DateTimeOffset.UtcNow,
        Error = "Provider credential changed while inspection was starting. Refresh billing again.",
        MetricSnapshot = new ProviderMetricSnapshot
        {
            ObservedAt = DateTimeOffset.UtcNow,
            Availability = ProviderMetricAvailability.Stale,
            BalanceAvailability = ProviderMetricAvailability.Stale,
            UsageAvailability = ProviderMetricAvailability.Stale,
            Diagnostic = "Provider credential changed while inspection was starting.",
        },
    };

    private static string? NormalizeFact(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? BuildBillingDiagnostic(
        ApiProviderSnapshot observation,
        bool allUnsupported,
        bool hasSupportedRecipe)
    {
        if (allUnsupported || (!hasSupportedRecipe && string.IsNullOrWhiteSpace(observation.Error)))
        {
            return "No supported billing endpoint was detected.";
        }

        return string.IsNullOrWhiteSpace(observation.Error)
            ? "The provider billing endpoint did not return a supported value."
            : SanitizeMessage(observation.Error);
    }

    /// <inheritdoc />
    public async Task<ApiProviderSnapshot> DiscoverModelsAsync(Guid providerProfileId, CancellationToken cancellationToken = default)
    {
        var profile = _apiProviderStore.GetById(providerProfileId);
        if (profile is null)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Error,
                Error = "Provider profile not found.",
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var secretOwnerId = profile.EndpointId ?? profile.Id;
        var apiKey = _secretStore.GetApiKey(secretOwnerId);
        var descriptor = !string.IsNullOrWhiteSpace(profile.CatalogProviderId)
            ? _catalogService.GetDescriptor(profile.CatalogProviderId)
            : null;
        var routeKey = !string.IsNullOrWhiteSpace(profile.SelectedRouteId)
            ? profile.SelectedRouteId
            : profile.BaseUrl;

        try
        {
            ApiProviderSnapshot snapshot;
            if (descriptor is not null)
            {
                snapshot = await _inspector.DiscoverModelsAsync(
                    descriptor,
                    profile.BaseUrl,
                    apiKey,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // The generic inspector is itself restricted to GET /models
                // and never guesses balance, usage, or inference endpoints.
                snapshot = await _inspector.DiscoverGenericUnknownModelsAsync(
                    profile.BaseUrl,
                    apiKey,
                    cancellationToken).ConfigureAwait(false);
            }

            if (snapshot.Models.Count > 0 && _modelCache is not null)
            {
                _modelCache.SetModels(profile.Id, routeKey, 1, snapshot.Models);
            }

            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Error,
                Error = SanitizeMessage(ex.Message),
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    private static string SanitizeMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return "Inspection failed.";
        if (message.Contains("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return "Inspection failed (credentials redacted).";
        }
        return message;
    }
}
