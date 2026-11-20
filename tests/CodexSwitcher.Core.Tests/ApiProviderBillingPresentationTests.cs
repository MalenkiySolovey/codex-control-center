using Llm.ProviderKit.Inspection;
using CodexSwitcher.App.ViewModels;

namespace CodexSwitcher.Core.Tests;

public sealed class ApiProviderBillingPresentationTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OpenRouterKeyMetricsDoNotRenderAsAccountBalance()
    {
        var profile = Profile(Snapshot(
            [Metric("spending", ProviderMetricKind.Spending, 25.500000000000000001m, ProviderMetricScope.ApiKey, ProviderMetricUnit.Currency("USD")),
             Metric("allowance", ProviderMetricKind.QuotaLimit, 100m, ProviderMetricScope.ApiKey, ProviderMetricUnit.Currency("USD"))],
            ProviderBillingStatus.Live, ProviderMetricAvailability.Unknown, ProviderMetricAvailability.Live));

        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true, credentialRevision: "revision-1");

        Assert.Equal("Account balance:", card.BalanceLabel);
        Assert.Equal("Unknown", card.BalanceSummary);
        Assert.Contains("API key spending 25.500000000000000001 USD", card.UsageSummary);
        Assert.Contains("allowance 100 USD", card.UsageSummary);
        Assert.DoesNotContain("Account balance", card.UsageSummary);
    }

    [Fact]
    public void HeJuUsageRetainsProviderSpecificUnitsAndPrecision()
    {
        var profile = Profile(Snapshot(
            [Metric("heju-usage", ProviderMetricKind.ProviderSpecificUsage, 1234.567891234567m,
                ProviderMetricScope.Unknown, ProviderMetricUnit.Provider("heju-display-units"))],
            ProviderBillingStatus.Live, ProviderMetricAvailability.Unknown, ProviderMetricAvailability.Live));

        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true, credentialRevision: "revision-1");

        Assert.Equal("Unknown", card.BalanceSummary);
        Assert.Contains("Usage 1234.567891234567 provider units", card.UsageSummary);
        Assert.DoesNotContain("USD", card.UsageSummary);
    }

    [Fact]
    public void OldSavedObservationIsDisplayedAsStaleAndLastKnown()
    {
        var oldMetric = Metric("balance-usd", ProviderMetricKind.AvailableBalance, 12.3456789m,
            ProviderMetricScope.Account, ProviderMetricUnit.Currency("USD"), ObservedAt.AddDays(-2));
        var profile = Profile(Snapshot([oldMetric], ProviderBillingStatus.Live,
            ProviderMetricAvailability.Live, ProviderMetricAvailability.Unknown, ObservedAt.AddDays(-2)));

        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true, credentialRevision: "revision-1");

        Assert.Contains("Stale · last known", card.BalanceSummary);
        Assert.Contains("12.3456789 USD", card.BalanceSummary);
        Assert.Contains("Last verified", card.BillingDiagnostic);
    }

    [Fact]
    public void FailedRefreshShowsLastKnownValueAsUnauthorized()
    {
        var metric = Metric("spending", ProviderMetricKind.Spending, 6m, ProviderMetricScope.ApiKey,
            ProviderMetricUnit.Currency("USD")) with
        { Availability = ProviderMetricAvailability.Cached };
        var profile = Profile(Snapshot([metric], ProviderBillingStatus.Unauthorized,
            ProviderMetricAvailability.Unknown, ProviderMetricAvailability.Unauthorized));

        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true, credentialRevision: "revision-1");

        Assert.Contains("Unauthorized · last known API key spending 6 USD", card.UsageSummary);
        Assert.Contains("Unauthorized", card.BillingDiagnostic);
    }

    [Fact]
    public void UnknownAndUnsupportedRemainDifferentAndLegacyNumbersAreNotPromoted()
    {
        var unknown = Profile(Snapshot([], ProviderBillingStatus.Unknown,
            ProviderMetricAvailability.Unknown, ProviderMetricAvailability.Unknown));
        var unsupported = Profile(Snapshot([], ProviderBillingStatus.Unsupported,
            ProviderMetricAvailability.Unsupported, ProviderMetricAvailability.Unsupported));
        var legacy = Profile(Snapshot([], ProviderBillingStatus.Live,
            ProviderMetricAvailability.Unknown, ProviderMetricAvailability.Unknown) with
        {
            Balance = 10m,
            Used = 2m,
            Currency = "USD",
        });

        var unknownCard = new ApiProviderItemViewModel(unknown, null, hasSecret: true, credentialRevision: "revision-1");
        var unsupportedCard = new ApiProviderItemViewModel(unsupported, null, hasSecret: true, credentialRevision: "revision-1");
        var legacyCard = new ApiProviderItemViewModel(legacy, null, hasSecret: true, credentialRevision: "revision-1");

        Assert.Equal("Unknown", unknownCard.BalanceSummary);
        Assert.Equal("Not supported", unsupportedCard.BalanceSummary);
        Assert.Equal("Unknown", legacyCard.BalanceSummary);
        Assert.Equal("Unknown", legacyCard.UsageSummary);
        Assert.Contains("legacy billing data", legacyCard.BillingDiagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CredentialRotationHidesSnapshotFromPreviousCredential()
    {
        var profile = Profile(Snapshot(
            [Metric("balance-usd", ProviderMetricKind.AvailableBalance, 3m,
                ProviderMetricScope.Account, ProviderMetricUnit.Currency("USD"))],
            ProviderBillingStatus.Live, ProviderMetricAvailability.Live, ProviderMetricAvailability.Unknown));

        var card = new ApiProviderItemViewModel(profile, null, hasSecret: true, credentialRevision: "revision-2");

        Assert.Equal("Unknown", card.BalanceSummary);
        Assert.Contains("another profile", card.BillingDiagnostic);
    }

    private static ApiProviderProfile Profile(ProviderBillingSnapshot snapshot)
    {
        var profile = new ApiProviderProfile
        {
            Id = snapshot.ProfileId,
            EndpointId = snapshot.CredentialOwnerId,
            CatalogProviderId = snapshot.ProviderId,
            BaseUrl = snapshot.BaseUrl ?? string.Empty,
            SelectedRouteId = snapshot.RouteId,
            LastBillingSnapshot = snapshot,
        };
        return profile;
    }

    private static ProviderBillingSnapshot Snapshot(IReadOnlyList<ProviderMetric> metrics,
        ProviderBillingStatus status, ProviderMetricAvailability balanceAvailability,
        ProviderMetricAvailability usageAvailability, DateTimeOffset? observedAt = null)
    {
        var profileId = Guid.NewGuid();
        return new ProviderBillingSnapshot
        {
            Metrics = metrics,
            ProfileId = profileId,
            ProviderId = "openrouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            RouteId = "primary",
            CredentialOwnerId = profileId,
            CredentialRevision = "revision-1",
            Availability = status switch
            {
                ProviderBillingStatus.Live => ProviderMetricAvailability.Live,
                ProviderBillingStatus.Partial => ProviderMetricAvailability.Partial,
                ProviderBillingStatus.Unauthorized => ProviderMetricAvailability.Unauthorized,
                ProviderBillingStatus.Unsupported => ProviderMetricAvailability.Unsupported,
                _ => ProviderMetricAvailability.Unknown,
            },
            BalanceAvailability = balanceAvailability,
            UsageAvailability = usageAvailability,
            ObservedAt = observedAt ?? ObservedAt,
            LastAttemptAt = observedAt ?? ObservedAt,
            Status = status,
            Diagnostic = status == ProviderBillingStatus.Unauthorized ? "HTTP 401 from synthetic provider." : null,
        };
    }

    private static ProviderMetric Metric(string id, ProviderMetricKind kind, decimal value,
        ProviderMetricScope scope, ProviderMetricUnit unit, DateTimeOffset? observedAt = null) => new()
        {
            Id = id,
            Kind = kind,
            Value = value,
            Scope = scope,
            Unit = unit,
            Availability = ProviderMetricAvailability.Live,
            ObservedAt = observedAt ?? ObservedAt,
            Source = "synthetic fixture",
        };
}
