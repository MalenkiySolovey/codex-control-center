using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Models;
using Llm.ProviderKit.Inspection;
using System.Text.Json;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexDesktopBalancePresentationMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private const string CodeHomeFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void FiniteApiKeyAllowancePreservesScopeCurrencyPeriodAndPercentage()
    {
        var profile = Profile();
        var remaining = Metric("remaining", ProviderMetricKind.QuotaRemaining, 6.01m, ProviderMetricScope.ApiKey,
            ProviderMetricUnit.Currency("USD"), "billing-cycle");
        var limit = Metric("limit", ProviderMetricKind.QuotaLimit, 283.23m, ProviderMetricScope.ApiKey,
            ProviderMetricUnit.Currency("USD"), "billing-cycle");
        profile.LastBillingSnapshot = Snapshot(profile, [remaining, limit]);

        var presentation = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-1", Now);

        Assert.Equal(CodexDesktopBalanceKind.ApiKeyAllowance, presentation.Kind);
        Assert.Equal(CodexDesktopBalanceState.Live, presentation.State);
        Assert.Equal(6.01m, presentation.Remaining);
        Assert.Equal(283.23m, presentation.Maximum);
        Assert.InRange(presentation.RemainingPercentage!.Value, 2.12m, 2.13m);
        Assert.Equal("Remaining $6.01 · 2.1%", presentation.ToCompactText(russian: false));
        Assert.Equal("Осталось 6,01 $ · 2,1%", presentation.ToCompactText(russian: true));
        Assert.Equal("API budget", presentation.ToExpandedTitle(russian: false));
        Assert.Collection(
            presentation.ToExpandedLines(russian: true).Take(3),
            line => Assert.Equal("Осталось 6,01 $", line),
            line => Assert.Equal("Максимум 283,23 $", line),
            line => Assert.Equal("Доля остатка 2,1%", line));
        Assert.Equal(CodeHomeFingerprint, presentation.ActiveCodeHomeFingerprint);
    }

    [Fact]
    public void CatalogDenominationFormatsKeyAllowanceAndRetainsRawPercentage()
    {
        var profile = Profile();
        var unit = ProviderMetricUnit.Provider("quota-units");
        var remaining = Metric("remaining", ProviderMetricKind.QuotaRemaining, 2_000_000m, ProviderMetricScope.ApiKey, unit)
            with
        {
            Display = new ProviderMetricDisplay(4m, "USD", "US$")
        };
        var limit = Metric("limit", ProviderMetricKind.QuotaLimit, 2_500_000m, ProviderMetricScope.ApiKey, unit)
            with
        {
            Display = new ProviderMetricDisplay(5m, "USD", "US$")
        };
        profile.LastBillingSnapshot = Snapshot(profile, [remaining, limit]);

        var presentation = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-1", Now);

        Assert.Equal(2_000_000m, presentation.Remaining);
        Assert.Equal(80m, presentation.RemainingPercentage);
        Assert.Equal("Remaining US$4.00 · 80%", presentation.ToCompactText(russian: false));
        Assert.Contains("Максимум 5,00 US$", presentation.ToExpandedLines(russian: true));
        Assert.Null(presentation.AccountBalance);
    }

    [Fact]
    public void FiniteKeyAllowanceShowsKeyUsageSeparatelyFromAccountUsage()
    {
        var profile = Profile();
        profile.LastBillingSnapshot = Snapshot(profile,
        [
            Metric("key-remaining", ProviderMetricKind.QuotaRemaining, 2_499_963m, ProviderMetricScope.ApiKey,
                ProviderMetricUnit.Provider("quota-units")),
            Metric("key-limit", ProviderMetricKind.QuotaLimit, 2_500_000m, ProviderMetricScope.ApiKey,
                ProviderMetricUnit.Provider("quota-units")),
            Metric("key-used", ProviderMetricKind.ProviderSpecificUsage, 37m, ProviderMetricScope.ApiKey,
                ProviderMetricUnit.Provider("quota-units")),
            Metric("account-used", ProviderMetricKind.ProviderSpecificUsage, 117.25m, ProviderMetricScope.Account,
                ProviderMetricUnit.Provider("heju-display-units")),
        ]);

        var presentation = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-1", Now);

        Assert.Equal(CodexDesktopBalanceKind.ApiKeyAllowance, presentation.Kind);
        Assert.Equal(37m, presentation.KeyUsage);
        Assert.Equal(117.25m, presentation.AccountUsage);
        Assert.Equal("Осталось 2 499 963 ед. квоты · <100%", presentation.ToCompactText(russian: true));
        Assert.Contains("Потрачено ключом: 37 ед. квоты", presentation.ToExpandedLines(russian: true));
        Assert.Contains("Расход аккаунта: 117,25 ед. HeJu", presentation.ToExpandedLines(russian: true));
    }

    [Fact]
    public void UnlimitedKeyShowsUnlimitedModeAndDoesNotDisplaySentinelZerosAsQuota()
    {
        var profile = Profile();
        profile.LastBillingSnapshot = Snapshot(profile,
        [
            new ProviderMetric
            {
                Id = "unlimited-key",
                Kind = ProviderMetricKind.QuotaLimit,
                Scope = ProviderMetricScope.ApiKey,
                Unit = ProviderMetricUnit.Provider("quota-units"),
                Availability = ProviderMetricAvailability.Live,
                ObservedAt = Now,
                IsUnlimited = true,
            },
            Metric("key-used", ProviderMetricKind.ProviderSpecificUsage, 37m, ProviderMetricScope.ApiKey,
                ProviderMetricUnit.Provider("quota-units")),
            Metric("account-used", ProviderMetricKind.ProviderSpecificUsage, 117.25m, ProviderMetricScope.Account,
                ProviderMetricUnit.Provider("heju-display-units")),
        ]);

        var presentation = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-1", Now);

        Assert.Equal(CodexDesktopBalanceKind.ApiKeyAllowance, presentation.Kind);
        Assert.True(presentation.QuotaUnlimited);
        Assert.Null(presentation.Remaining);
        Assert.Null(presentation.Maximum);
        Assert.Null(presentation.RemainingPercentage);
        Assert.Equal("Безлимитный API-ключ", presentation.ToCompactText(russian: true));
        Assert.Contains("Лимит ключа: без ограничений", presentation.ToExpandedLines(russian: true));
        Assert.Contains("Баланс аккаунта: неизвестен", presentation.ToExpandedLines(russian: true));
        Assert.Contains("Потрачено ключом: 37 ед. квоты", presentation.ToExpandedLines(russian: true));
        Assert.Contains("Расход аккаунта: 117,25 ед. HeJu", presentation.ToExpandedLines(russian: true));
    }

    [Fact]
    public void UnlimitedKeyUsesOnlyAnExplicitlyScopedWallet()
    {
        var profile = Profile();
        profile.LastBillingSnapshot = Snapshot(profile,
        [
            Metric("key-unlimited", ProviderMetricKind.QuotaLimit, 0m, ProviderMetricScope.ApiKey,
                ProviderMetricUnit.Provider("quota-units")) with { Value = null, IsUnlimited = true },
            Metric("wallet", ProviderMetricKind.AvailableBalance, 8m, ProviderMetricScope.Account,
                ProviderMetricUnit.Currency("USD")),
        ]);

        var presentation = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-1", Now);

        Assert.Equal("Unlimited API key · $8.00", presentation.ToCompactText(russian: false));
        Assert.Contains("Account balance: $8.00", presentation.ToExpandedLines(russian: false));
        Assert.Null(presentation.RemainingPercentage);
    }

    [Fact]
    public void FiniteZeroGrantKeepsExplicitQuotaWithoutAnUndefinedPercentage()
    {
        var profile = Profile();
        var unit = ProviderMetricUnit.Provider("quota-units");
        profile.LastBillingSnapshot = Snapshot(profile,
        [
            Metric("key-limit", ProviderMetricKind.QuotaLimit, 0m, ProviderMetricScope.ApiKey, unit),
            Metric("key-remaining", ProviderMetricKind.QuotaRemaining, 0m, ProviderMetricScope.ApiKey, unit),
        ]);

        var presentation = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-1", Now);

        Assert.Equal(CodexDesktopBalanceKind.ApiKeyAllowance, presentation.Kind);
        Assert.Equal("Remaining 0 quota units", presentation.ToCompactText(russian: false));
        Assert.Contains("Maximum 0 quota units", presentation.ToExpandedLines(russian: false));
        Assert.Null(presentation.RemainingPercentage);
    }

    [Fact]
    public void WalletFromAFormerUrlCannotBeShownAndAgedAmountsAreMarkedCached()
    {
        var profile = Profile();
        var snapshot = Snapshot(profile,
        [
            Metric("wallet", ProviderMetricKind.AvailableBalance, 8m, ProviderMetricScope.Account,
                ProviderMetricUnit.Currency("USD")),
        ]);
        profile.LastBillingSnapshot = snapshot;

        var cached = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-1", Now.AddMinutes(3));
        Assert.Equal("API budget · $8.00 · Cached", cached.ToCompactText(russian: false));

        profile.LastBillingSnapshot = snapshot with { BaseUrl = "https://previous.example.invalid/v1" };
        var staleRoute = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-1", Now);
        Assert.Equal("desktop.balance.billing-identity-mismatch", staleRoute.DiagnosticCode);
        Assert.Null(staleRoute.AccountBalance);
    }

    [Fact]
    public void RendererPayloadContainsOnlySanitizedDisplayStrings()
    {
        var presentation = new CodexDesktopBalancePresentation
        {
            Kind = CodexDesktopBalanceKind.ApiKeyAllowance,
            State = CodexDesktopBalanceState.Live,
            ProviderLabel = "Synthetic provider",
            ProfileLabel = "Test profile",
            ScopeLabel = "ApiKey",
            Remaining = 6.01m,
            Maximum = 283.23m,
            RemainingPercentage = 2.1m,
            UnitCode = "USD",
            Period = "billing-cycle",
            Source = "synthetic-fixture",
            ActiveCodeHomeFingerprint = CodeHomeFingerprint,
        };

        var payload = CodexDesktopBalanceRendererPayload.FromPresentation(presentation, russian: true);
        var json = JsonSerializer.Serialize(payload);

        Assert.NotNull(payload);
        Assert.Equal("Осталось 6,01 $ · 2,1%", payload!.Compact);
        Assert.Equal("Бюджет API", payload.Title);
        Assert.Contains("Максимум 283,23 $", payload.Lines);
        Assert.Contains("billing-cycle", json);
        Assert.DoesNotContain("Source", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Fingerprint", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileLabel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BaseUrl", json, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RendererPayloadOmitsChatGptPresentationAndFiltersUnsafeDisplayText()
    {
        var hidden = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.ChatGpt(Guid.NewGuid()), CodeHomeFingerprint, null, Now);
        var unsafeLabel = new CodexDesktopBalancePresentation
        {
            Kind = CodexDesktopBalanceKind.AccountBalance,
            State = CodexDesktopBalanceState.Live,
            AccountBalance = 12m,
            UnitCode = "USD",
            ProviderLabel = "unexpected\ncontent",
        };

        Assert.Null(CodexDesktopBalanceRendererPayload.FromPresentation(hidden, russian: true));
        var payload = CodexDesktopBalanceRendererPayload.FromPresentation(unsafeLabel, russian: true);
        Assert.NotNull(payload);
        Assert.DoesNotContain(payload!.Lines, line => line.Contains("unexpected", StringComparison.Ordinal));
        Assert.All(payload.Lines, line => Assert.DoesNotContain(line, char.IsControl));
    }

    [Fact]
    public void NativeUsageKeepsKeyScopeCurrencyAndFreshnessWithStructuredRows()
    {
        var presentation = new CodexDesktopBalancePresentation
        {
            Kind = CodexDesktopBalanceKind.ApiKeyAllowance,
            State = CodexDesktopBalanceState.Stale,
            ProviderLabel = "Synthetic provider",
            ProfileLabel = "Test profile",
            ScopeLabel = "ApiKey",
            Remaining = 3.50m,
            Maximum = 5m,
            RemainingPercentage = 70m,
            UnitCode = "USD",
            KeyUsage = 1.50m,
            KeyUsageUnitCode = "USD",
            ObservedAt = Now,
            Source = "internal-source",
            ActiveCodeHomeFingerprint = CodeHomeFingerprint,
        };

        var native = Assert.IsType<CodexDesktopNativeUsagePayload>(
            CodexDesktopNativeUsagePayload.FromPresentation(presentation, russian: false));
        Assert.Equal(70m, native.RemainingPercentage);
        Assert.Contains(new CodexDesktopNativeUsageRow("Remaining", "$3.50"), native.Rows);
        Assert.Contains(new CodexDesktopNativeUsageRow("Maximum", "$5.00"), native.Rows);
        Assert.Contains(new CodexDesktopNativeUsageRow("Key usage", "$1.50"), native.Rows);
        Assert.Contains(new CodexDesktopNativeUsageRow("Scope", "API key"), native.Rows);
        Assert.Contains(new CodexDesktopNativeUsageRow("Profile", "Test profile"), native.Rows);
        Assert.Contains(native.Rows, row => row.Label == "Observed");
        Assert.Contains(new CodexDesktopNativeUsageRow("Stale", string.Empty), native.Rows);
        var json = JsonSerializer.Serialize(native);
        Assert.DoesNotContain("internal-source", json, StringComparison.Ordinal);
        Assert.DoesNotContain(CodeHomeFingerprint, json, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeUsageShowsUnlimitedAndUnknownWalletWithoutAFiniteRing()
    {
        var presentation = new CodexDesktopBalancePresentation
        {
            Kind = CodexDesktopBalanceKind.ApiKeyAllowance,
            State = CodexDesktopBalanceState.Partial,
            QuotaUnlimited = true,
            RemainingPercentage = 100m,
        };

        var native = Assert.IsType<CodexDesktopNativeUsagePayload>(
            CodexDesktopNativeUsagePayload.FromPresentation(presentation, russian: false));
        Assert.Null(native.RemainingPercentage);
        Assert.Contains(new CodexDesktopNativeUsageRow("Key limit", "unlimited"), native.Rows);
        Assert.Contains(new CodexDesktopNativeUsageRow("Account balance", "Unknown"), native.Rows);
        Assert.DoesNotContain(native.Rows, row => row.Label == "Maximum");

        var hidden = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.ChatGpt(Guid.NewGuid()), CodeHomeFingerprint, null, Now);
        Assert.Null(CodexDesktopNativeUsagePayload.FromPresentation(hidden, russian: false));
    }

    [Fact]
    public void AccountBalanceIsSeparateAndNeverInventsALimitOrPercentage()
    {
        var profile = Profile();
        profile.LastBillingSnapshot = Snapshot(profile,
        [
            Metric("account-balance", ProviderMetricKind.AvailableBalance, 283.23m, ProviderMetricScope.Account,
                ProviderMetricUnit.Currency("USD")),
        ]);

        var presentation = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-1", Now);

        Assert.Equal(CodexDesktopBalanceKind.AccountBalance, presentation.Kind);
        Assert.Equal(283.23m, presentation.AccountBalance);
        Assert.Null(presentation.Maximum);
        Assert.Null(presentation.RemainingPercentage);
        Assert.Equal("API budget · $283.23", presentation.ToCompactText(russian: false));
        Assert.Equal("Бюджет API · 283,23 $", presentation.ToCompactText(russian: true));
        Assert.Equal("Доступный баланс 283,23 $", presentation.ToExpandedLines(russian: true)[0]);
    }

    [Fact]
    public void ProviderSpecificUsageRetainsItsUnitAndDoesNotBecomeCurrency()
    {
        var profile = Profile();
        profile.LastBillingSnapshot = Snapshot(profile,
        [
            Metric("credits", ProviderMetricKind.ProviderSpecificUsage, 1234.567m, ProviderMetricScope.ApiKey,
                ProviderMetricUnit.Provider("heju-display-units")),
        ]);

        var presentation = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-1", Now);

        Assert.Equal(CodexDesktopBalanceKind.UsageOnly, presentation.Kind);
        Assert.Equal(1234.567m, presentation.Usage);
        Assert.Equal("heju-display-units", presentation.UnitCode);
        Assert.DoesNotContain("USD", presentation.ToCompactText(russian: false));
    }

    [Fact]
    public void AccountWideProviderSpecificUsageIsLabeledAsAccountUsage()
    {
        var profile = Profile();
        profile.LastBillingSnapshot = Snapshot(profile,
        [
            Metric("account-used", ProviderMetricKind.ProviderSpecificUsage, 117.25m, ProviderMetricScope.Account,
                ProviderMetricUnit.Provider("heju-display-units")),
        ]);

        var presentation = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-1", Now);

        Assert.Equal(CodexDesktopBalanceKind.UsageOnly, presentation.Kind);
        Assert.Equal("Расход аккаунта 117,25 ед. HeJu", presentation.ToCompactText(russian: true));
        Assert.Equal("Расход аккаунта 117,25 ед. HeJu", presentation.ToExpandedLines(russian: true)[0]);
        Assert.DoesNotContain("Потрачено ключом", presentation.ToExpandedLines(russian: true));
    }

    [Fact]
    public void CredentialRotationAndUnverifiedCodeHomeSuppressAllAmounts()
    {
        var profile = Profile();
        profile.LastBillingSnapshot = Snapshot(profile,
        [
            Metric("balance", ProviderMetricKind.AvailableBalance, 10m, ProviderMetricScope.Account,
                ProviderMetricUnit.Currency("USD")),
        ]);

        var rotated = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-2", Now);
        var unknownHome = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), "C:\\private\\CODEX_HOME", "revision-1", Now);

        Assert.Equal(CodexDesktopBalanceKind.StatusOnly, rotated.Kind);
        Assert.Equal("desktop.balance.billing-identity-mismatch", rotated.DiagnosticCode);
        Assert.Null(rotated.AccountBalance);
        Assert.Equal("desktop.balance.code-home-unverified", unknownHome.DiagnosticCode);
        Assert.Null(unknownHome.AccountBalance);
    }

    [Fact]
    public void OldAndUnsupportedObservationsPreserveTheirStateWithoutFalsifyingFreshness()
    {
        var staleProfile = Profile();
        var old = Metric("balance", ProviderMetricKind.AvailableBalance, 9m, ProviderMetricScope.Account,
            ProviderMetricUnit.Currency("USD")) with
        { ObservedAt = Now.AddDays(-2) };
        staleProfile.LastBillingSnapshot = Snapshot(staleProfile, [old], observedAt: Now.AddDays(-2));
        var stale = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(staleProfile), CodeHomeFingerprint, "revision-1", Now);

        var unsupportedProfile = Profile();
        unsupportedProfile.LastBillingSnapshot = Snapshot(unsupportedProfile, [], ProviderBillingStatus.Unsupported);
        var unsupported = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(unsupportedProfile), CodeHomeFingerprint, "revision-1", Now);

        Assert.Equal(CodexDesktopBalanceState.Stale, stale.State);
        Assert.Equal(9m, stale.AccountBalance);
        Assert.Equal(CodexDesktopBalanceState.Unsupported, unsupported.State);
        Assert.Equal(CodexDesktopBalanceKind.StatusOnly, unsupported.Kind);
    }

    [Fact]
    public void ChatGptAndExternalRoutesNeverReceiveTheThirdPartyBalancePayload()
    {
        var chatGpt = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.ChatGpt(Guid.NewGuid()), CodeHomeFingerprint, null, Now);
        var external = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.External("company-proxy"), CodeHomeFingerprint, null, Now);

        Assert.Equal(CodexDesktopBalanceKind.Hidden, chatGpt.Kind);
        Assert.Equal(CodexDesktopBalanceKind.Hidden, external.Kind);
        Assert.DoesNotContain("openrouter", chatGpt.ToCompactText(russian: false), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MismatchedAllowancePeriodOrCurrencyDoesNotCreateAValue()
    {
        var profile = Profile();
        var remaining = Metric("remaining", ProviderMetricKind.QuotaRemaining, 6m, ProviderMetricScope.ApiKey,
            ProviderMetricUnit.Currency("USD"), "current-month");
        var limit = Metric("limit", ProviderMetricKind.QuotaLimit, 20m, ProviderMetricScope.ApiKey,
            ProviderMetricUnit.Currency("EUR"), "previous-month");
        profile.LastBillingSnapshot = Snapshot(profile, [remaining, limit]);

        var presentation = CodexDesktopBalancePresentationMapper.Create(
            new ActiveTarget.Api(profile), CodeHomeFingerprint, "revision-1", Now);

        Assert.Equal(CodexDesktopBalanceKind.StatusOnly, presentation.Kind);
        Assert.Null(presentation.Remaining);
        Assert.Null(presentation.Maximum);
        Assert.Null(presentation.RemainingPercentage);
    }

    [Fact]
    public void CodeHomeFingerprintIsOneWayAndNeverReturnsTheSourcePath()
    {
        const string path = "C:\\synthetic\\codex-home";

        var fingerprint = CodexDesktopBalancePresentationMapper.CreateCodeHomeFingerprint(path);

        Assert.Equal(64, fingerprint.Length);
        Assert.DoesNotContain("synthetic", fingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fingerprint, CodexDesktopBalancePresentationMapper.CreateCodeHomeFingerprint(path));
    }

    private static ApiProviderProfile Profile() => new()
    {
        Id = Guid.NewGuid(),
        EndpointId = Guid.NewGuid(),
        CatalogProviderId = "openrouter",
        StableCodexProviderId = "switchboard_test",
        SelectedRouteId = "primary",
        Nickname = "Synthetic route",
        BaseUrl = "https://example.invalid/v1",
        Status = ApiProviderProfileStatus.Active,
    };

    private static ProviderBillingSnapshot Snapshot(
        ApiProviderProfile profile,
        IReadOnlyList<ProviderMetric> metrics,
        ProviderBillingStatus status = ProviderBillingStatus.Live,
        DateTimeOffset? observedAt = null) => new()
        {
            Metrics = metrics,
            ProfileId = profile.Id,
            ProviderId = profile.CatalogProviderId,
            BaseUrl = profile.BaseUrl,
            RouteId = profile.SelectedRouteId,
            CredentialOwnerId = profile.EndpointId ?? profile.Id,
            CredentialRevision = "revision-1",
            ObservedAt = observedAt ?? Now,
            LastAttemptAt = observedAt ?? Now,
            Status = status,
        };

    private static ProviderMetric Metric(
        string id,
        ProviderMetricKind kind,
        decimal value,
        ProviderMetricScope scope,
        ProviderMetricUnit unit,
        string? period = null) => new()
        {
            Id = id,
            Kind = kind,
            Value = value,
            Scope = scope,
            Unit = unit,
            Availability = ProviderMetricAvailability.Live,
            ObservedAt = Now,
            Period = period,
            PeriodSemantics = period is null ? ProviderMetricPeriodSemantics.Unknown : ProviderMetricPeriodSemantics.BillingCycle,
            Source = "synthetic test source",
        };
}
