using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using Llm.ProviderKit.Inspection;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
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
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Normalized outcome of inspecting an API provider.
/// Decouples presentation and domain logic from provider-specific response JSON.
/// </summary>
public sealed record ApiProviderSnapshot
{
    /// <summary>Connection and reachability status.</summary>
    public HealthStatus ConnectionStatus { get; init; } = HealthStatus.Unknown;

    /// <summary>Available models discovered via inspection (if supported).</summary>
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();

    /// <summary>Remaining balance in currency units if known.</summary>
    public decimal? Balance { get; init; }

    /// <summary>Currency code (e.g. "USD", "CNY") if known.</summary>
    public string? Currency { get; init; }

    /// <summary>Total credits used if reported by provider.</summary>
    public decimal? UsedCredits { get; init; }

    /// <summary>Remaining credits if reported separately from balance.</summary>
    public decimal? RemainingCredits { get; init; }

    /// <summary>Hard credit limit if reported by provider.</summary>
    public decimal? CreditLimit { get; init; }

    /// <summary>Generic usage metric if reported.</summary>
    public decimal? Usage { get; init; }

    /// <summary>Expected quota or credit reset timestamp (UTC) if known.</summary>
    public DateTimeOffset? ResetAt { get; init; }

    /// <summary>Status of individual capabilities (e.g. models, balance, usage).</summary>
    public IReadOnlyDictionary<string, CapabilityStatus> Capabilities { get; init; } =
        new Dictionary<string, CapabilityStatus>();

    /// <summary>Timestamp when this snapshot was captured (UTC).</summary>
    public DateTimeOffset LastCheckedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Sanitized error message if inspection failed. Never contains secrets or URLs with tokens.</summary>
    public string? Error { get; init; }

    /// <summary>Typed, provenance-preserving metrics from ProviderKit.</summary>
    public ProviderMetricSnapshot MetricSnapshot { get; init; } = new();
}

/// <summary>
/// Persistable, credential-free provider billing observation for card rendering.
/// It intentionally contains normalized facts only, never raw provider payloads.
/// </summary>
public sealed record ProviderBillingSnapshot
{
    /// <summary>Typed values are authoritative; flattened fields below remain for old readers.</summary>
    public IReadOnlyList<ProviderMetric> Metrics { get; init; } = Array.Empty<ProviderMetric>();
    public ProviderMetricAvailability Availability { get; init; } = ProviderMetricAvailability.Unknown;
    public ProviderMetricAvailability BalanceAvailability { get; init; } = ProviderMetricAvailability.Unknown;
    public ProviderMetricAvailability UsageAvailability { get; init; } = ProviderMetricAvailability.Unknown;

    /// <summary>Identity binds cached values to one profile, provider route, and credential revision.</summary>
    public Guid ProfileId { get; init; }
    public string? ProviderId { get; init; }
    public string? BaseUrl { get; init; }
    public string? RouteId { get; init; }
    public Guid? CredentialOwnerId { get; init; }
    public string? CredentialRevision { get; init; }

    public decimal? Balance { get; init; }
    public decimal? Used { get; init; }
    public decimal? Limit { get; init; }
    public decimal? Remaining { get; init; }
    public string? Currency { get; init; }
    public string? Unit { get; init; }
    public string? Period { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset LastAttemptAt { get; init; }
    public ProviderBillingStatus Status { get; init; } = ProviderBillingStatus.Unknown;
    public string? Diagnostic { get; init; }
}

public enum ProviderBillingStatus
{
    Unknown = 0,
    Live = 1,
    Cached = 2,
    Unsupported = 3,
    AuthRejected = 4,
    Unavailable = 5,
    Partial = 6,
    Stale = 7,
    Unauthorized = 8,
    InvalidResponse = 9,
}
