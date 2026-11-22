using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;
using Llm.ProviderKit.Inspection;

namespace CodexSwitcher.Core.Providers.Services;

public enum CodexDesktopBalanceKind
{
    Hidden = 0,
    ApiKeyAllowance = 1,
    AccountBalance = 2,
    UsageOnly = 3,
    StatusOnly = 4,
}

public enum CodexDesktopBalanceState
{
    Live = 0,
    Partial = 1,
    Cached = 2,
    Stale = 3,
    Unknown = 4,
    Unsupported = 5,
    Unavailable = 6,
    Unauthorized = 7,
}

/// <summary>
/// Small read-only payload for a verified Codex Desktop renderer. It contains
/// presentation facts only, never credentials, provider URLs, or raw responses.
/// </summary>
public sealed record CodexDesktopBalancePresentation
{
    public CodexDesktopBalanceKind Kind { get; init; }
    public CodexDesktopBalanceState State { get; init; }
    public string? ProviderLabel { get; init; }
    public string? ProfileLabel { get; init; }
    public string? ScopeLabel { get; init; }
    public decimal? Remaining { get; init; }
    public decimal? Maximum { get; init; }
    public ProviderMetricDisplay? RemainingDisplay { get; init; }
    public ProviderMetricDisplay? MaximumDisplay { get; init; }
    public decimal? RemainingPercentage { get; init; }
    public bool QuotaUnlimited { get; init; }
    public decimal? KeyUsage { get; init; }
    public string? KeyUsageUnitCode { get; init; }
    public ProviderMetricDisplay? KeyUsageDisplay { get; init; }
    public decimal? AccountUsage { get; init; }
    public string? AccountUsageUnitCode { get; init; }
    public decimal? AccountBalance { get; init; }
    public string? AccountBalanceUnitCode { get; init; }
    public decimal? Usage { get; init; }
    public ProviderMetricDisplay? UsageDisplay { get; init; }
    public string? UnitCode { get; init; }
    public string? Period { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? ActiveCodeHomeFingerprint { get; init; }
    public string DiagnosticCode { get; init; } = "desktop.balance.unknown";

    public string ToCompactText(bool russian)
    {
        var culture = russian ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.GetCultureInfo("en-US");
        var text = Kind switch
        {
            CodexDesktopBalanceKind.ApiKeyAllowance when QuotaUnlimited =>
                (russian ? "Безлимитный API-ключ" : "Unlimited API key") +
                (AccountBalance is { } wallet ? $" · {FormatAmount(wallet, AccountBalanceUnitCode, culture, russian)}" : string.Empty),
            CodexDesktopBalanceKind.ApiKeyAllowance when Remaining is not null =>
                $"{(russian ? "Осталось" : "Remaining")} {FormatAmount(Remaining.Value, UnitCode, culture, russian, RemainingDisplay)}" +
                (RemainingPercentage is null ? string.Empty : $" · {FormatPercentage(RemainingPercentage.Value, culture)}%"),
            CodexDesktopBalanceKind.AccountBalance when AccountBalance is not null =>
                $"{(russian ? "Бюджет API" : "API budget")} · {FormatAmount(AccountBalance.Value, UnitCode, culture, russian)}",
            CodexDesktopBalanceKind.UsageOnly when Usage is not null =>
                $"{UsageLabel(russian, ScopeLabel)} {FormatAmount(Usage.Value, UnitCode, culture, russian, UsageDisplay)}",
            CodexDesktopBalanceKind.StatusOnly => StateLabel(russian),
            _ => string.Empty,
        };
        return text.Length == 0 || Kind == CodexDesktopBalanceKind.StatusOnly || State == CodexDesktopBalanceState.Live
            ? text
            : $"{text} · {StateLabel(russian)}";
    }

    public string ToExpandedTitle(bool russian) => Kind switch
    {
        CodexDesktopBalanceKind.ApiKeyAllowance or CodexDesktopBalanceKind.AccountBalance => russian ? "Бюджет API" : "API budget",
        CodexDesktopBalanceKind.UsageOnly => russian ? "Использование API" : "API usage",
        CodexDesktopBalanceKind.StatusOnly => StateLabel(russian),
        _ => string.Empty,
    };

    public IReadOnlyList<string> ToExpandedLines(bool russian)
    {
        var culture = russian ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.GetCultureInfo("en-US");
        var lines = new List<string>();
        switch (Kind)
        {
            case CodexDesktopBalanceKind.ApiKeyAllowance when Remaining is not null:
                lines.Add($"{(russian ? "Осталось" : "Remaining")} {FormatAmount(Remaining.Value, UnitCode, culture, russian, RemainingDisplay)}");
                if (Maximum is not null)
                {
                    lines.Add($"{(russian ? "Максимум" : "Maximum")} {FormatAmount(Maximum.Value, UnitCode, culture, russian, MaximumDisplay)}");
                }

                if (RemainingPercentage is not null)
                {
                    lines.Add($"{(russian ? "Доля остатка" : "Remaining share")} {FormatPercentage(RemainingPercentage.Value, culture)}%");
                }

                AddUsageLine(lines, russian, russian ? "Потрачено ключом" : "Key usage", KeyUsage, KeyUsageUnitCode, culture, KeyUsageDisplay);
                AddAccountUsageLine(lines, russian, AccountUsage, AccountUsageUnitCode, culture);
                break;
            case CodexDesktopBalanceKind.ApiKeyAllowance when QuotaUnlimited:
                lines.Add(russian ? "Лимит ключа: без ограничений" : "Key limit: unlimited");
                var accountLabel = russian ? "Баланс аккаунта" : "Account balance";
                var wallet = AccountBalance is { } observedWallet
                    ? FormatAmount(observedWallet, AccountBalanceUnitCode, culture, russian)
                    : russian ? "неизвестен" : "Unknown";
                lines.Add($"{accountLabel}: {wallet}");
                AddUsageLine(lines, russian, russian ? "Потрачено ключом" : "Key usage", KeyUsage, KeyUsageUnitCode, culture, KeyUsageDisplay);
                AddAccountUsageLine(lines, russian, AccountUsage, AccountUsageUnitCode, culture);
                break;
            case CodexDesktopBalanceKind.AccountBalance when AccountBalance is not null:
                lines.Add($"{(russian ? "Доступный баланс" : "Available balance")} {FormatAmount(AccountBalance.Value, UnitCode, culture, russian)}");
                AddAccountUsageLine(lines, russian, AccountUsage, AccountUsageUnitCode, culture);
                break;
            case CodexDesktopBalanceKind.UsageOnly when Usage is not null:
                lines.Add($"{UsageLabel(russian, ScopeLabel)} {FormatAmount(Usage.Value, UnitCode, culture, russian, UsageDisplay)}");
                break;
            case CodexDesktopBalanceKind.StatusOnly:
                lines.Add(StateLabel(russian));
                break;
        }

        AddLabel(lines, russian ? "Провайдер" : "Provider", ProviderLabel);
        AddLabel(lines, russian ? "Профиль" : "Profile", ProfileLabel);
        if (ScopeLabel is not null)
        {
            var scope = ScopeLabel switch
            {
                nameof(ProviderMetricScope.ApiKey) => russian ? "ключ API" : "API key",
                nameof(ProviderMetricScope.Account) => russian ? "аккаунт" : "account",
                _ => ScopeLabel,
            };
            lines.Add($"{(russian ? "Область" : "Scope")}: {scope}");
        }

        AddLabel(lines, russian ? "Период" : "Period", Period);
        if (ObservedAt is not null)
        {
            lines.Add($"{(russian ? "Получено" : "Observed")}: {ObservedAt.Value.ToLocalTime().ToString("g", culture)}");
        }

        if (ResetAt is not null)
        {
            lines.Add($"{(russian ? "Сброс" : "Resets")}: {ResetAt.Value.ToLocalTime().ToString("g", culture)}");
        }

        if (ExpiresAt is not null)
        {
            lines.Add($"{(russian ? "Ключ действует до" : "Key expires")}: {ExpiresAt.Value.ToLocalTime().ToString("g", culture)}");
        }

        if (Kind != CodexDesktopBalanceKind.StatusOnly)
        {
            lines.Add(StateLabel(russian));
        }

        return lines;
    }

    public string StateLabel(bool russian) => State switch
    {
        CodexDesktopBalanceState.Live => russian ? "Актуально" : "Live",
        CodexDesktopBalanceState.Partial => russian ? "Частичные данные" : "Partial",
        CodexDesktopBalanceState.Cached => russian ? "Кэш" : "Cached",
        CodexDesktopBalanceState.Stale => russian ? "Устарело" : "Stale",
        CodexDesktopBalanceState.Unsupported => russian ? "Не поддерживается" : "Unsupported",
        CodexDesktopBalanceState.Unavailable => russian ? "Недоступно" : "Unavailable",
        CodexDesktopBalanceState.Unauthorized => russian ? "Нет доступа" : "Unauthorized",
        _ => russian ? "Неизвестно" : "Unknown",
    };

    private static string Format(decimal value, CultureInfo culture) => value.ToString("0.############################", culture);

    private static string FormatPercentage(decimal value, CultureInfo culture)
    {
        var formatted = value.ToString("0.#", culture);
        if (decimal.TryParse(formatted, culture, out var rounded))
        {
            if (value > 0m && rounded == 0m) return $"<{0.1m.ToString("0.#", culture)}";
            if (value < 100m && rounded == 100m) return "<100";
        }

        return formatted;
    }

    private static string FormatAmount(decimal value, string? unitCode, CultureInfo culture, bool russian,
        ProviderMetricDisplay? display = null)
    {
        if (ProviderQuotaDisplayFormatter.Format(display, culture, symbolAfterAmount: russian) is { } converted)
            return converted;

        var normalizedUnit = unitCode?.Trim();
        if (string.IsNullOrEmpty(normalizedUnit))
        {
            return Format(value, culture);
        }

        var currencySymbol = normalizedUnit.ToUpperInvariant() switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "RUB" => "₽",
            "JPY" or "CNY" => "¥",
            "CAD" => "CA$",
            "AUD" => "A$",
            _ => null,
        };
        if (currencySymbol is not null)
        {
            var amount = value.ToString("N2", culture);
            return russian ? $"{amount} {currencySymbol}" : $"{currencySymbol}{amount}";
        }

        if (normalizedUnit.Equals("quota-units", StringComparison.OrdinalIgnoreCase))
        {
            var amount = value.ToString("#,0.############################", culture);
            return russian ? $"{amount} ед. квоты" : $"{amount} quota units";
        }

        if (normalizedUnit.Equals("heju-display-units", StringComparison.OrdinalIgnoreCase))
        {
            return russian
                ? $"{Format(value, culture)} ед. HeJu"
                : $"{Format(value, culture)} HeJu units";
        }

        return $"{Format(value, culture)} {normalizedUnit}";
    }

    private static string UsageLabel(bool russian, string? scope) =>
        string.Equals(scope, nameof(ProviderMetricScope.Account), StringComparison.Ordinal)
            ? russian ? "Расход аккаунта" : "Account usage"
            : string.Equals(scope, nameof(ProviderMetricScope.ApiKey), StringComparison.Ordinal)
                ? russian ? "Потрачено ключом" : "Key usage"
                : russian ? "Использовано" : "Used";

    private static void AddUsageLine(List<string> lines, bool russian, string label, decimal? value, string? unitCode,
        CultureInfo culture, ProviderMetricDisplay? display = null)
    {
        if (value is not null)
        {
            lines.Add($"{label}: {FormatAmount(value.Value, unitCode, culture, russian, display)}");
        }
    }

    private static void AddAccountUsageLine(List<string> lines, bool russian, decimal? value, string? unitCode,
        CultureInfo culture) =>
        AddUsageLine(lines, russian, russian ? "Расход аккаунта" : "Account usage", value, unitCode, culture);

    private static void AddLabel(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}: {value}");
        }
    }
}

/// <summary>
/// Binds a stored billing observation to the currently resolved API route and
/// credential revision before reducing it to renderer-safe display data.
/// </summary>
public static class CodexDesktopBalancePresentationMapper
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan LiveFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);

    public static string CreateCodeHomeFingerprint(string codeHomePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHomePath);
        var normalizedPath = Path.GetFullPath(codeHomePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            normalizedPath = normalizedPath.ToUpperInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
    }

    public static CodexDesktopBalancePresentation Create(
        ActiveTarget activeTarget,
        string? activeCodeHomeFingerprint,
        string? currentCredentialRevision,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(activeTarget);
        if (activeTarget is not ActiveTarget.Api { Profile: not null } apiTarget)
        {
            return new()
            {
                Kind = CodexDesktopBalanceKind.Hidden,
                State = CodexDesktopBalanceState.Unknown,
                DiagnosticCode = "desktop.balance.non-api-route",
            };
        }

        var profile = apiTarget.Profile;
        var snapshot = profile.LastBillingSnapshot;
        if (string.IsNullOrWhiteSpace(activeCodeHomeFingerprint) ||
            !Regex.IsMatch(activeCodeHomeFingerprint, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
        {
            return Status(CodexDesktopBalanceState.Unknown, "desktop.balance.code-home-unverified", null);
        }

        if (profile.Status != ApiProviderProfileStatus.Active ||
            string.IsNullOrWhiteSpace(profile.StableCodexProviderId) ||
            string.IsNullOrWhiteSpace(currentCredentialRevision) ||
            snapshot is null)
        {
            return Status(CodexDesktopBalanceState.Unknown, "desktop.balance.identity-unavailable", activeCodeHomeFingerprint);
        }

        var expectedCredentialOwnerId = profile.EndpointId ?? profile.Id;
        if (snapshot.ProfileId != profile.Id ||
            !string.Equals(snapshot.ProviderId, profile.CatalogProviderId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.BaseUrl, profile.BaseUrl, StringComparison.Ordinal) ||
            !string.Equals(snapshot.RouteId, profile.SelectedRouteId, StringComparison.Ordinal) ||
            snapshot.CredentialOwnerId != expectedCredentialOwnerId ||
            !string.Equals(snapshot.CredentialRevision, currentCredentialRevision, StringComparison.Ordinal))
        {
            return Status(CodexDesktopBalanceState.Unknown, "desktop.balance.billing-identity-mismatch", activeCodeHomeFingerprint);
        }

        var state = ResolveState(snapshot, now, snapshot.Metrics);
        var metrics = snapshot.Metrics.ToArray();
        var limit = ExactlyOne(metrics, ProviderMetricKind.QuotaLimit, ProviderMetricScope.ApiKey);
        var remaining = ExactlyOne(metrics, ProviderMetricKind.QuotaRemaining, ProviderMetricScope.ApiKey);
        var keyUsage = ExactlyOneUsage(metrics, ProviderMetricScope.ApiKey, now);
        var accountUsage = ExactlyOneUsage(metrics, ProviderMetricScope.Account, now);
        var accountBalance = ExactlyOne(metrics, ProviderMetricKind.AvailableBalance, ProviderMetricScope.Account);
        if (accountBalance is not null && (!IsCurrency(accountBalance) || !IsDisplayable(accountBalance, now)))
            accountBalance = null;

        var unlimited = limit is not null && IsUnlimitedDisplayable(limit, now);
        if (limit is not null && (unlimited ||
            (remaining is not null && AreCompatibleAllowanceMetrics(remaining, limit, now))))
        {
            var keyRemaining = unlimited ? null : remaining;
            var wallet = unlimited ? accountBalance : null;
            var sharedDenomination = limit.Display is { IsValid: true } limitDisplay &&
                keyRemaining?.Display is { IsValid: true } remainingDisplay &&
                limitDisplay.Currency == remainingDisplay.Currency && limitDisplay.Symbol == remainingDisplay.Symbol;
            decimal? percentage = limit.Value > 0m && keyRemaining?.Value is { } amount
                ? amount / limit.Value.Value * 100m
                : null;
            state = ResolveState(snapshot, now, [limit, .. NonNullMetrics(keyRemaining, keyUsage, accountUsage, wallet)]);
            return new()
            {
                Kind = CodexDesktopBalanceKind.ApiKeyAllowance,
                State = state,
                ProviderLabel = SafeLabel(profile.CatalogProviderId),
                ProfileLabel = SafeLabel(profile.Nickname),
                ScopeLabel = nameof(ProviderMetricScope.ApiKey),
                Remaining = keyRemaining?.Value,
                Maximum = limit.Value,
                RemainingDisplay = sharedDenomination ? keyRemaining?.Display : null,
                MaximumDisplay = sharedDenomination ? limit.Display : null,
                RemainingPercentage = percentage,
                QuotaUnlimited = unlimited,
                KeyUsage = keyUsage?.Value,
                KeyUsageUnitCode = DisplayUnitCode(keyUsage?.Unit),
                KeyUsageDisplay = keyUsage?.Display,
                AccountUsage = accountUsage?.Value,
                AccountUsageUnitCode = DisplayUnitCode(accountUsage?.Unit),
                AccountBalance = wallet?.Value,
                AccountBalanceUnitCode = DisplayUnitCode(wallet?.Unit),
                UnitCode = DisplayUnitCode(limit.Unit),
                Period = SafeLabel(limit.Period),
                Source = SafeLabel(limit.Source),
                ObservedAt = limit.ObservedAt,
                ResetAt = limit.ResetAt,
                ExpiresAt = limit.ExpiresAt,
                ActiveCodeHomeFingerprint = activeCodeHomeFingerprint,
                DiagnosticCode = unlimited ? "desktop.balance.api-key-unlimited" : "desktop.balance.api-key-allowance",
            };
        }

        if (accountBalance is not null)
        {
            state = ResolveState(snapshot, now, [accountBalance, .. NonNullMetrics(accountUsage)]);
            return new()
            {
                Kind = CodexDesktopBalanceKind.AccountBalance,
                State = state,
                ProviderLabel = SafeLabel(profile.CatalogProviderId),
                ProfileLabel = SafeLabel(profile.Nickname),
                ScopeLabel = ProviderMetricScope.Account.ToString(),
                AccountBalance = accountBalance.Value,
                AccountUsage = accountUsage?.Value,
                AccountUsageUnitCode = DisplayUnitCode(accountUsage?.Unit),
                UnitCode = accountBalance.Unit.Code,
                Period = SafeLabel(accountBalance.Period),
                Source = SafeLabel(accountBalance.Source),
                ObservedAt = accountBalance.ObservedAt,
                ResetAt = accountBalance.ResetAt,
                ActiveCodeHomeFingerprint = activeCodeHomeFingerprint,
                DiagnosticCode = "desktop.balance.account-balance",
            };
        }

        var usage = metrics.Where(IsUsageMetric)
            .Where(metric => metric.Scope is ProviderMetricScope.ApiKey or ProviderMetricScope.Account)
            .Where(metric => metric.Unit.Kind != ProviderMetricUnitKind.Unknown && IsDisplayable(metric, now))
            .OrderBy(metric => metric.Kind == ProviderMetricKind.Spending ? 0 : 1)
            .FirstOrDefault();
        if (usage is not null)
        {
            state = ResolveState(snapshot, now, [usage]);
            return new()
            {
                Kind = CodexDesktopBalanceKind.UsageOnly,
                State = state,
                ProviderLabel = SafeLabel(profile.CatalogProviderId),
                ProfileLabel = SafeLabel(profile.Nickname),
                ScopeLabel = usage.Scope.ToString(),
                Usage = usage.Value,
                UsageDisplay = usage.Scope == ProviderMetricScope.ApiKey ? usage.Display : null,
                UnitCode = DisplayUnitCode(usage.Unit),
                Period = SafeLabel(usage.Period),
                Source = SafeLabel(usage.Source),
                ObservedAt = usage.ObservedAt,
                ResetAt = usage.ResetAt,
                ExpiresAt = usage.ExpiresAt,
                ActiveCodeHomeFingerprint = activeCodeHomeFingerprint,
                DiagnosticCode = "desktop.balance.usage-only",
            };
        }

        return Status(state == CodexDesktopBalanceState.Live ? CodexDesktopBalanceState.Unknown : state,
            "desktop.balance.metric-unavailable", activeCodeHomeFingerprint);
    }

    private static CodexDesktopBalancePresentation Status(
        CodexDesktopBalanceState state,
        string diagnosticCode,
        string? codeHomeFingerprint) => new()
        {
            Kind = CodexDesktopBalanceKind.StatusOnly,
            State = state,
            ActiveCodeHomeFingerprint = codeHomeFingerprint,
            DiagnosticCode = diagnosticCode,
        };

    private static CodexDesktopBalanceState ResolveState(
        ProviderBillingSnapshot snapshot,
        DateTimeOffset now,
        IReadOnlyList<ProviderMetric> metrics)
    {
        var age = now - snapshot.ObservedAt;
        if (age < -MaximumFutureSkew)
        {
            return CodexDesktopBalanceState.Unknown;
        }

        if (age > StaleAfter)
        {
            return CodexDesktopBalanceState.Stale;
        }

        var storedState = snapshot.Status switch
        {
            ProviderBillingStatus.Live => CodexDesktopBalanceState.Live,
            ProviderBillingStatus.Partial => CodexDesktopBalanceState.Partial,
            ProviderBillingStatus.Cached => CodexDesktopBalanceState.Cached,
            ProviderBillingStatus.Stale => CodexDesktopBalanceState.Stale,
            ProviderBillingStatus.Unsupported => CodexDesktopBalanceState.Unsupported,
            ProviderBillingStatus.AuthRejected or ProviderBillingStatus.Unauthorized => CodexDesktopBalanceState.Unauthorized,
            ProviderBillingStatus.Unavailable or ProviderBillingStatus.InvalidResponse => CodexDesktopBalanceState.Unavailable,
            _ => CodexDesktopBalanceState.Unknown,
        };
        if (storedState is not (CodexDesktopBalanceState.Unknown or CodexDesktopBalanceState.Live))
        {
            return storedState;
        }

        if (metrics.Any(metric => metric.Availability == ProviderMetricAvailability.Stale || now - metric.ObservedAt > StaleAfter))
        {
            return CodexDesktopBalanceState.Stale;
        }

        if (age > LiveFor || metrics.Any(metric => metric.Availability == ProviderMetricAvailability.Cached ||
            now - metric.ObservedAt > LiveFor))
        {
            return CodexDesktopBalanceState.Cached;
        }

        if (metrics.Count > 0 && metrics.All(metric => metric.Availability == ProviderMetricAvailability.Live))
        {
            return CodexDesktopBalanceState.Live;
        }

        return storedState;
    }

    private static ProviderMetric? ExactlyOne(
        IReadOnlyList<ProviderMetric> metrics,
        ProviderMetricKind kind,
        ProviderMetricScope scope)
    {
        var matches = metrics.Where(metric => metric.Kind == kind && metric.Scope == scope).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static ProviderMetric? ExactlyOneUsage(
        IReadOnlyList<ProviderMetric> metrics,
        ProviderMetricScope scope,
        DateTimeOffset now)
    {
        var matches = metrics.Where(metric => IsUsageMetric(metric) && metric.Scope == scope &&
            metric.Value.HasValue && IsDisplayable(metric, now)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static IEnumerable<ProviderMetric> NonNullMetrics(params ProviderMetric?[] metrics) =>
        metrics.Where(metric => metric is not null).Cast<ProviderMetric>();

    private static bool IsUnlimitedDisplayable(ProviderMetric metric, DateTimeOffset now) =>
        metric.IsUnlimited && metric.Value is null &&
        metric.ObservedAt <= now + MaximumFutureSkew &&
        metric.Availability is ProviderMetricAvailability.Live or ProviderMetricAvailability.Cached or ProviderMetricAvailability.Stale;

    private static bool AreCompatibleAllowanceMetrics(ProviderMetric remaining, ProviderMetric limit, DateTimeOffset now) =>
        remaining.Scope == ProviderMetricScope.ApiKey &&
        remaining.Unit == limit.Unit &&
        remaining.PeriodSemantics == limit.PeriodSemantics &&
        string.Equals(remaining.Period, limit.Period, StringComparison.Ordinal) &&
        remaining.PeriodStartAt == limit.PeriodStartAt &&
        remaining.PeriodEndAt == limit.PeriodEndAt &&
        remaining.ResetAt == limit.ResetAt &&
        remaining.ObservedAt == limit.ObservedAt &&
        !remaining.IsUnlimited && !limit.IsUnlimited &&
        remaining.Value >= 0m && limit.Value >= 0m && remaining.Value <= limit.Value &&
        IsDisplayable(remaining, now) && IsDisplayable(limit, now) &&
        (remaining.Unit.Kind == ProviderMetricUnitKind.Currency || remaining.Unit.Kind == ProviderMetricUnitKind.ProviderSpecific ||
         remaining.Unit.Kind is ProviderMetricUnitKind.Requests or ProviderMetricUnitKind.Tokens);

    private static bool IsCurrency(ProviderMetric metric) =>
        metric.Unit.Kind == ProviderMetricUnitKind.Currency && !string.IsNullOrWhiteSpace(metric.Unit.Code);

    private static string? DisplayUnitCode(ProviderMetricUnit? unit) => unit?.Kind switch
    {
        ProviderMetricUnitKind.Currency => unit.Code,
        ProviderMetricUnitKind.ProviderSpecific when unit.Code is { Length: 3 } code && code.All(char.IsAsciiLetter) =>
            $"units ({code})",
        ProviderMetricUnitKind.ProviderSpecific => unit.Code,
        ProviderMetricUnitKind.Requests => "requests",
        ProviderMetricUnitKind.Tokens => "tokens",
        _ => null,
    };

    private static bool IsDisplayable(ProviderMetric metric, DateTimeOffset now) =>
        metric.Value.HasValue &&
        (metric.AllowsNegative || metric.Value.Value >= 0m) &&
        metric.ObservedAt <= now + MaximumFutureSkew &&
        metric.Availability is ProviderMetricAvailability.Live or ProviderMetricAvailability.Cached or ProviderMetricAvailability.Stale;

    private static bool IsUsageMetric(ProviderMetric metric) => metric.Kind is
        ProviderMetricKind.Spending or ProviderMetricKind.ProviderCredits or ProviderMetricKind.ProviderSpecificUsage or
        ProviderMetricKind.RequestCount or ProviderMetricKind.TokenCount;

    private static string? SafeLabel(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 96 || value.Any(char.IsControl) ? null : value.Trim();
}
