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
    public decimal? RemainingPercentage { get; init; }
    public decimal? AccountBalance { get; init; }
    public decimal? Usage { get; init; }
    public string? UnitCode { get; init; }
    public string? Period { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    public string? ActiveCodeHomeFingerprint { get; init; }
    public string DiagnosticCode { get; init; } = "desktop.balance.unknown";

    public string ToCompactText(bool russian)
    {
        var culture = russian ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.GetCultureInfo("en-US");
        return Kind switch
        {
            CodexDesktopBalanceKind.ApiKeyAllowance when Remaining is not null =>
                $"{(russian ? "Осталось" : "Remaining")} {FormatAmount(Remaining.Value, UnitCode, culture, russian)}" +
                (RemainingPercentage is null ? string.Empty : $" · {FormatPercentage(RemainingPercentage.Value, culture)}%"),
            CodexDesktopBalanceKind.AccountBalance when AccountBalance is not null =>
                $"{(russian ? "Бюджет API" : "API budget")} · {FormatAmount(AccountBalance.Value, UnitCode, culture, russian)}",
            CodexDesktopBalanceKind.UsageOnly when Usage is not null =>
                $"{(russian ? "Использовано" : "Used")} {FormatAmount(Usage.Value, UnitCode, culture, russian)}",
            CodexDesktopBalanceKind.StatusOnly => StateLabel(russian),
            _ => string.Empty,
        };
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
                lines.Add($"{(russian ? "Осталось" : "Remaining")} {FormatAmount(Remaining.Value, UnitCode, culture, russian)}");
                if (Maximum is not null)
                {
                    lines.Add($"{(russian ? "Максимум" : "Maximum")} {FormatAmount(Maximum.Value, UnitCode, culture, russian)}");
                }

                if (RemainingPercentage is not null)
                {
                    lines.Add($"{(russian ? "Доля остатка" : "Remaining share")} {FormatPercentage(RemainingPercentage.Value, culture)}%");
                }

                break;
            case CodexDesktopBalanceKind.AccountBalance when AccountBalance is not null:
                lines.Add($"{(russian ? "Доступный баланс" : "Available balance")} {FormatAmount(AccountBalance.Value, UnitCode, culture, russian)}");
                break;
            case CodexDesktopBalanceKind.UsageOnly when Usage is not null:
                lines.Add($"{(russian ? "Использовано" : "Used")} {FormatAmount(Usage.Value, UnitCode, culture, russian)}");
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
        return value > 0m && decimal.TryParse(formatted, culture, out var rounded) && rounded == 0m
            ? $"<{0.1m.ToString("0.#", culture)}"
            : formatted;
    }

    private static string FormatAmount(decimal value, string? unitCode, CultureInfo culture, bool russian)
    {
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

        return $"{Format(value, culture)} {normalizedUnit}";
    }

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
            !string.Equals(snapshot.RouteId, profile.SelectedRouteId, StringComparison.Ordinal) ||
            snapshot.CredentialOwnerId != expectedCredentialOwnerId ||
            !string.Equals(snapshot.CredentialRevision, currentCredentialRevision, StringComparison.Ordinal))
        {
            return Status(CodexDesktopBalanceState.Unknown, "desktop.balance.billing-identity-mismatch", activeCodeHomeFingerprint);
        }

        var state = ResolveState(snapshot, now, snapshot.Metrics);
        var metrics = snapshot.Metrics.Where(metric => metric.Value.HasValue).ToArray();
        var remaining = ExactlyOne(metrics, ProviderMetricKind.QuotaRemaining, ProviderMetricScope.ApiKey);
        var limit = ExactlyOne(metrics, ProviderMetricKind.QuotaLimit, ProviderMetricScope.ApiKey);
        if (remaining is not null && limit is not null && AreCompatibleAllowanceMetrics(remaining, limit, now))
        {
            decimal? percentage = limit.Value is > 0m
                ? Math.Clamp(remaining.Value!.Value / limit.Value.Value * 100m, 0m, 100m)
                : null;
            state = ResolveState(snapshot, now, [remaining, limit]);
            return new()
            {
                Kind = CodexDesktopBalanceKind.ApiKeyAllowance,
                State = state,
                ProviderLabel = SafeLabel(profile.CatalogProviderId),
                ProfileLabel = SafeLabel(profile.Nickname),
                ScopeLabel = remaining.Scope.ToString(),
                Remaining = remaining.Value,
                Maximum = limit.Value,
                RemainingPercentage = percentage,
                UnitCode = remaining.Unit.Code ?? remaining.Unit.Kind.ToString(),
                Period = SafeLabel(remaining.Period),
                Source = SafeLabel(remaining.Source),
                ObservedAt = remaining.ObservedAt,
                ResetAt = remaining.ResetAt,
                ActiveCodeHomeFingerprint = activeCodeHomeFingerprint,
                DiagnosticCode = "desktop.balance.api-key-allowance",
            };
        }

        var accountBalance = ExactlyOne(metrics, ProviderMetricKind.AvailableBalance, ProviderMetricScope.Account);
        if (accountBalance is not null && IsCurrency(accountBalance) && IsDisplayable(accountBalance, now))
        {
            state = ResolveState(snapshot, now, [accountBalance]);
            return new()
            {
                Kind = CodexDesktopBalanceKind.AccountBalance,
                State = state,
                ProviderLabel = SafeLabel(profile.CatalogProviderId),
                ProfileLabel = SafeLabel(profile.Nickname),
                ScopeLabel = ProviderMetricScope.Account.ToString(),
                AccountBalance = accountBalance.Value,
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
                UnitCode = usage.Unit.Code ?? usage.Unit.Kind.ToString(),
                Period = SafeLabel(usage.Period),
                Source = SafeLabel(usage.Source),
                ObservedAt = usage.ObservedAt,
                ResetAt = usage.ResetAt,
                ActiveCodeHomeFingerprint = activeCodeHomeFingerprint,
                DiagnosticCode = "desktop.balance.usage-only",
            };
        }

        return Status(state, "desktop.balance.metric-unavailable", activeCodeHomeFingerprint);
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

        if (metrics.Any(metric => metric.Availability == ProviderMetricAvailability.Cached))
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

    private static bool AreCompatibleAllowanceMetrics(ProviderMetric remaining, ProviderMetric limit, DateTimeOffset now) =>
        remaining.Scope == ProviderMetricScope.ApiKey &&
        remaining.Unit == limit.Unit &&
        remaining.PeriodSemantics == limit.PeriodSemantics &&
        string.Equals(remaining.Period, limit.Period, StringComparison.Ordinal) &&
        remaining.PeriodStartAt == limit.PeriodStartAt &&
        remaining.PeriodEndAt == limit.PeriodEndAt &&
        remaining.ResetAt == limit.ResetAt &&
        remaining.ObservedAt == limit.ObservedAt &&
        remaining.Value >= 0m && limit.Value > 0m && remaining.Value <= limit.Value &&
        IsDisplayable(remaining, now) && IsDisplayable(limit, now) &&
        (remaining.Unit.Kind == ProviderMetricUnitKind.Currency || remaining.Unit.Kind == ProviderMetricUnitKind.ProviderSpecific ||
         remaining.Unit.Kind is ProviderMetricUnitKind.Requests or ProviderMetricUnitKind.Tokens);

    private static bool IsCurrency(ProviderMetric metric) =>
        metric.Unit.Kind == ProviderMetricUnitKind.Currency && !string.IsNullOrWhiteSpace(metric.Unit.Code);

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
