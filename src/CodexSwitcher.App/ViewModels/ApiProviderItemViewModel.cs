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
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using Llm.ProviderKit.Inspection;
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
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace CodexSwitcher.App.ViewModels;

/// <summary>Semantic visual state of an API provider card. Controls border and background styling.</summary>
public enum ApiProviderCardVisualState
{
    /// <summary>Provider is the active routing target.</summary>
    ActiveRouting,
    /// <summary>Provider has no API key configured.</summary>
    KeyRequired,
    /// <summary>Normal inactive provider card.</summary>
    Normal,
}

/// <summary>A card-level model choice; Slug is the exact value persisted and routed.</summary>
public sealed record ApiProviderQuickModelChoice(string Slug, string DisplayName)
{
    public string DisplayText => string.Equals(DisplayName, Slug, StringComparison.Ordinal)
        ? Slug
        : $"{DisplayName} · {Slug}";
}

public sealed record ApiProviderModelSelectionRequest(ApiProviderItemViewModel Item, string ModelSlug);

/// <summary>
/// Presentation ViewModel for an API Provider card in the UI.
/// Exposes safe key previews, capability tri-state facts, and routing state.
/// </summary>
public sealed partial class ApiProviderItemViewModel : ObservableObject
{
    private static readonly TimeSpan BillingStaleAfter = TimeSpan.FromHours(24);
    private long _latestRefreshGeneration;
    private bool _latestRefreshIsTerminal;
    private string? _billingCredentialRevision;

    private static Strings Loc => Strings.Current;

    public ApiProviderProfile Profile { get; private set; }
    public ProviderDescriptor? Descriptor { get; private set; }

    public Guid Id => Profile.Id;
    public string StableCodexProviderId => Profile.StableCodexProviderId;
    public string? CatalogProviderId => Profile.CatalogProviderId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    [NotifyPropertyChangedFor(nameof(CardVisualState))]
    public partial bool IsTargetActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefreshBilling))]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    [NotifyPropertyChangedFor(nameof(CardVisualState))]
    public partial bool HasSecret { get; set; }

    /// <summary>
    /// Current semantic visual state for the full provider card border and background.
    /// </summary>
    public ApiProviderCardVisualState CardVisualState =>
        IsTargetActive ? ApiProviderCardVisualState.ActiveRouting :
        !HasSecret ? ApiProviderCardVisualState.KeyRequired :
        ApiProviderCardVisualState.Normal;

    public void UpdateRoutingState(bool isTargetActive)
    {
        IsTargetActive = isTargetActive;
    }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    public partial string CatalogProviderName { get; set; }

    [ObservableProperty]
    public partial string BaseUrl { get; set; }

    [ObservableProperty]
    public partial string SelectedRoute { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedSelectedModel))]
    [NotifyPropertyChangedFor(nameof(ShowDisplayedSelectedModel))]
    public partial string SelectedModel { get; set; }

    public string DisplayedSelectedModel => string.IsNullOrWhiteSpace(SelectedModel)
        ? Loc.NoSelectableApiModels
        : HasSelectableModels && !QuickSelectableModels.Any(model =>
            string.Equals(model.Slug, SelectedModel, StringComparison.Ordinal))
            ? $"{SelectedModel} ({Loc.ModelUnavailable})"
            : SelectedModel;

    [ObservableProperty]
    public partial string KeyPreview { get; set; }

    [ObservableProperty]
    public partial ApiProviderProfileStatus Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectModel))]
    public partial bool IsLoadingModels { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectModel))]
    public partial bool IsSelectingModel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectModel))]
    public partial bool IsEditingProvider { get; set; }

    public ObservableCollection<ApiProviderQuickModelChoice> QuickSelectableModels { get; } = [];
    public bool HasSelectableModels => QuickSelectableModels.Count > 0;
    public bool ShowDisplayedSelectedModel => !HasSelectableModels || !QuickSelectableModels.Any(model =>
        string.Equals(model.Slug, SelectedModel, StringComparison.Ordinal));
    public bool CanSelectModel => HasSelectableModels && !IsLoadingModels && !IsSelectingModel && !IsEditingProvider;

    [ObservableProperty]
    public partial string ModelsStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelsSummary))]
    public partial int ModelsReportedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelsSummary))]
    public partial int ModelsEnabledCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelsSummary))]
    public partial DateTimeOffset? LastModelsRefreshAt { get; set; }

    /// <summary>Compact, persisted-inventory based model summary for the card.</summary>
    public string ModelsSummary => ModelsReportedCount > 0
        ? $"{ModelsReportedCount} reported · {ModelsEnabledCount} enabled"
        : ModelsEnabledCount > 0
            ? $"{ModelsEnabledCount} enabled"
            : "No models discovered";

    [ObservableProperty]
    public partial string BalanceSummary { get; set; } = "Unknown";

    [ObservableProperty]
    public partial string BalanceLabel { get; set; } = "Account balance:";

    [ObservableProperty]
    public partial string UsageSummary { get; set; } = "Unknown";

    [ObservableProperty]
    public partial string UsageLabel { get; set; } = "Usage / quota:";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefreshBilling))]
    public partial bool IsRefreshingBilling { get; set; }

    public bool CanRefreshBilling => HasSecret && !IsRefreshingBilling;

    [ObservableProperty]
    public partial string BillingDiagnostic { get; set; } = "No supported billing endpoint was detected.";

    [ObservableProperty]
    public partial string Initials { get; set; } = "AP";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoutePoolLabel))]
    public partial string? RoutePoolLabel { get; set; }

    public bool HasRoutePoolLabel => !string.IsNullOrWhiteSpace(RoutePoolLabel);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompatibilityBadge))]
    [NotifyPropertyChangedFor(nameof(CompatibilityBadgeText))]
    [NotifyPropertyChangedFor(nameof(CompatibilityBadgeBackground))]
    public partial CodexCompatibilityLevel CompatibilityLevel { get; set; } = CodexCompatibilityLevel.Unknown;

    public bool HasCompatibilityBadge => CompatibilityLevel != CodexCompatibilityLevel.Unknown;

    public string CompatibilityBadgeText => CompatibilityLevel switch
    {
        CodexCompatibilityLevel.CodexCompatible => "Codex Verified",
        CodexCompatibilityLevel.PartiallyCompatible => "Partial Compatibility",
        CodexCompatibilityLevel.NotCompatible => "Not Compatible",
        _ => string.Empty
    };

    private static readonly Lazy<Microsoft.UI.Xaml.Media.SolidColorBrush> SuccessBrush = new(() =>
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 65)));
    private static readonly Lazy<Microsoft.UI.Xaml.Media.SolidColorBrush> WarningBrush = new(() =>
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 216, 59, 1)));
    private static readonly Lazy<Microsoft.UI.Xaml.Media.SolidColorBrush> DangerBrush = new(() =>
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 168, 0, 0)));

    public Microsoft.UI.Xaml.Media.Brush CompatibilityBadgeBackground => CompatibilityLevel switch
    {
        CodexCompatibilityLevel.CodexCompatible => SuccessBrush.Value,
        CodexCompatibilityLevel.PartiallyCompatible => WarningBrush.Value,
        _ => DangerBrush.Value
    };

    public IReadOnlyList<ProviderRoute> Routes => Descriptor?.Routes ?? [];
    public bool CanToggleRoute => Routes.Count > 1;
    public bool CanSwitch => !IsTargetActive && HasSecret;

    public string ActiveBadgeLabel => Loc.BadgeActiveNow;
    public string MissingCredentialLabel => Loc.ApiKeyRequired;
    public string InUseLabel => Loc.InUse;
    public string SwitchLabel => Loc.Switch;
    public string ContinueOnLabel => Loc.ContinueOn;
    public string ContinueOnTooltip => Loc.ContinueOnTooltip;
    public string RefreshLabel => Loc.RefreshAll;
    public string MoreActionsLabel => Loc.MoreActions;
    public string EditLabel => Loc.Rename;
    public string RotateKeyLabel => Loc.RotateKey;
    public string RemoveLabel => Loc.RemoveCredential;
    public string MoveUpLabel => Loc.MoveUp;
    public string MoveDownLabel => Loc.MoveDown;
    public string ModelLabel => Loc.ModelLabel;
    public string ModelSelectionProgressLabel => Loc.SelectingApiModel;
    public string ModelSelectionTooltip => Loc.ApiModelQuickSelectTooltip;

    public ApiProviderItemViewModel(
        ApiProviderProfile profile,
        ProviderDescriptor? descriptor,
        bool hasSecret = false,
        bool isTargetActive = false,
        string? credentialRevision = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Descriptor = descriptor;
        HasSecret = hasSecret;
        IsTargetActive = isTargetActive;
        _billingCredentialRevision = credentialRevision;

        DisplayName = profile.DisplayName;
        BaseUrl = profile.BaseUrl;
        SelectedModel = profile.GetEffectiveSelectedModel() ?? string.Empty;
        KeyPreview = profile.KeyPreview;
        Status = profile.Status;
        RoutePoolLabel = profile.RoutePoolLabel;
        CompatibilityLevel = profile.CompatibilityLevel;

        CatalogProviderName = descriptor?.DisplayName ?? (string.IsNullOrWhiteSpace(profile.CatalogProviderId) ? "Custom Provider" : profile.CatalogProviderId);
        SelectedRoute = ResolveRouteName(profile, descriptor);
        Initials = ComputeInitials(CatalogProviderName, DisplayName);

        ApplyTruthfulCapabilityFacts(descriptor);
        RefreshInventoryPresentation(profile);
        ApplyBillingSnapshot(profile.LastBillingSnapshot, fromCache: true);
    }

    public void UpdateProfile(ApiProviderProfile updated, ProviderDescriptor? descriptor, bool hasSecret, bool isTargetActive)
        => UpdateProfile(updated, descriptor, hasSecret, isTargetActive, _billingCredentialRevision);

    public void UpdateProfile(ApiProviderProfile updated, ProviderDescriptor? descriptor, bool hasSecret, bool isTargetActive,
        string? credentialRevision)
    {
        Profile = updated ?? throw new ArgumentNullException(nameof(updated));
        Descriptor = descriptor;
        HasSecret = hasSecret;
        IsTargetActive = isTargetActive;
        _billingCredentialRevision = credentialRevision;

        DisplayName = updated.DisplayName;
        BaseUrl = updated.BaseUrl;
        KeyPreview = updated.KeyPreview;
        Status = updated.Status;
        RoutePoolLabel = updated.RoutePoolLabel;
        CompatibilityLevel = updated.CompatibilityLevel;

        CatalogProviderName = descriptor?.DisplayName ?? (string.IsNullOrWhiteSpace(updated.CatalogProviderId) ? "Custom Provider" : updated.CatalogProviderId);
        SelectedRoute = ResolveRouteName(updated, descriptor);
        Initials = ComputeInitials(CatalogProviderName, DisplayName);

        ApplyTruthfulCapabilityFacts(descriptor);
        RefreshInventoryPresentation(updated);
        // The ComboBox binds SelectedValue against QuickSelectableModels.
        // Refresh its items first so the new persisted slug can be matched
        // after an inventory refresh or model selection.
        SelectedModel = updated.GetEffectiveSelectedModel() ?? string.Empty;
        // RefreshInventoryPresentation clears and repopulates the bound item
        // collection. WinUI may clear the ComboBox selection during that
        // update; if the persisted slug did not change, the generated setter
        // raises no notification, so re-publish it after the choices exist.
        OnPropertyChanged(nameof(SelectedModel));
        ApplyBillingSnapshot(updated.LastBillingSnapshot, fromCache: true);
    }

    public void ApplyBillingSnapshot(ProviderBillingSnapshot? snapshot, bool fromCache)
    {
        if (snapshot is null)
        {
            var hasCatalogRecipe = Descriptor?.Capabilities?.Balance?.Status == CapabilityStatus.Supported ||
                                   Descriptor?.Capabilities?.Usage?.Status == CapabilityStatus.Supported;
            BalanceLabel = "Account balance:";
            UsageLabel = "Usage / quota:";
            BalanceSummary = Descriptor?.Capabilities?.Balance?.Status == CapabilityStatus.Unsupported
                ? "Not supported"
                : "Unknown";
            UsageSummary = Descriptor?.Capabilities?.Usage?.Status == CapabilityStatus.Unsupported
                ? "Not supported"
                : "Unknown";
            BillingDiagnostic = hasCatalogRecipe
                ? "A billing recipe is available. Refresh balance & usage to inspect it."
                : "No supported billing endpoint was detected.";
            return;
        }

        var hasLegacyFacts = snapshot.Balance.HasValue || snapshot.Used.HasValue || snapshot.Limit.HasValue || snapshot.Remaining.HasValue;
        if (snapshot.ProfileId == Guid.Empty && hasLegacyFacts)
        {
            BalanceLabel = "Account balance:";
            UsageLabel = "Usage / quota:";
            BalanceSummary = "Unknown";
            UsageSummary = "Unknown";
            BillingDiagnostic = "Saved legacy billing data has no verified unit, scope, or credential identity. Refresh to verify it.";
            return;
        }

        if (!MatchesCurrentIdentity(snapshot))
        {
            BalanceLabel = "Account balance:";
            UsageLabel = "Usage / quota:";
            BalanceSummary = "Unknown";
            UsageSummary = "Unknown";
            BillingDiagnostic = "Saved billing data belongs to another profile, provider route, or credential. Refresh to verify it.";
            return;
        }

        var hasTypedMetrics = snapshot.Metrics is { Count: > 0 };
        if (!hasTypedMetrics && hasLegacyFacts)
        {
            BalanceLabel = "Account balance:";
            UsageLabel = "Usage / quota:";
            BalanceSummary = "Unknown";
            UsageSummary = "Unknown";
            BillingDiagnostic = "Saved legacy billing data has no verified unit or scope. Refresh to verify it.";
            return;
        }

        var hasValues = hasTypedMetrics && snapshot.Metrics.Any(metric => metric.Value.HasValue || metric.IsUnlimited);
        var age = DateTimeOffset.UtcNow - snapshot.ObservedAt;
        var status = ResolveDisplayStatus(snapshot.Status, fromCache, age, hasValues);
        var balances = snapshot.Metrics.Where(metric => metric.Kind == ProviderMetricKind.AvailableBalance && metric.Value.HasValue).ToArray();
        var usage = snapshot.Metrics.Where(metric => IsUsageMetric(metric.Kind) && (metric.Value.HasValue ||
            metric is { Kind: ProviderMetricKind.QuotaLimit, Scope: ProviderMetricScope.ApiKey, IsUnlimited: true })).ToArray();
        BalanceLabel = balances.Length == 0 ? "Account balance:" : BalanceScopeLabel(balances);
        UsageLabel = "Usage / quota:";
        BalanceSummary = FormatBalance(balances, snapshot.BalanceAvailability, status, fromCache, snapshot.Status);
        UsageSummary = FormatUsage(usage, snapshot.UsageAvailability, status, fromCache, snapshot.Status);

        var observationTime = snapshot.ObservedAt == default ? "unknown" :
            snapshot.ObservedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        var attemptTime = snapshot.LastAttemptAt == default ? observationTime :
            snapshot.LastAttemptAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        var freshness = snapshot.Status == ProviderBillingStatus.Partial ? " Latest response was partial." : string.Empty;
        var detail = string.IsNullOrWhiteSpace(snapshot.Diagnostic) ? string.Empty : $" {snapshot.Diagnostic}";
        BillingDiagnostic = $"{StatusLabel(status)}.{detail} Last verified {observationTime}; last attempt {attemptTime}.{freshness}";
    }

    private bool MatchesCurrentIdentity(ProviderBillingSnapshot snapshot) =>
        snapshot.ProfileId == Profile.Id && snapshot.CredentialOwnerId == (Profile.EndpointId ?? Profile.Id) &&
        string.Equals(snapshot.CredentialRevision, _billingCredentialRevision, StringComparison.Ordinal) &&
        string.Equals(snapshot.ProviderId, Profile.CatalogProviderId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(snapshot.BaseUrl, Profile.BaseUrl, StringComparison.Ordinal) &&
        string.Equals(snapshot.RouteId, Profile.SelectedRouteId, StringComparison.OrdinalIgnoreCase);

    private static ProviderBillingStatus ResolveDisplayStatus(ProviderBillingStatus status, bool fromCache,
        TimeSpan age, bool hasValues)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (fromCache && status is ProviderBillingStatus.Live or ProviderBillingStatus.Partial or ProviderBillingStatus.Cached or ProviderBillingStatus.Stale)
        {
            return age > BillingStaleAfter ? ProviderBillingStatus.Stale : ProviderBillingStatus.Cached;
        }

        return fromCache && hasValues && age > BillingStaleAfter
            ? ProviderBillingStatus.Stale
            : status;
    }

    private static string FormatBalance(ProviderMetric[] metrics, ProviderMetricAvailability availability,
        ProviderBillingStatus status, bool fromCache, ProviderBillingStatus storedStatus)
    {
        if (metrics.Length == 0) return EmptyMetricState(availability, status);
        var body = string.Join(" · ", metrics.Take(3).Select(metric => FormatMetricValue(metric)));
        return AddMetricFreshness(body, status, fromCache, storedStatus, metrics.Any(metric => metric.Availability == ProviderMetricAvailability.Cached));
    }

    private static string FormatUsage(ProviderMetric[] metrics, ProviderMetricAvailability availability,
        ProviderBillingStatus status, bool fromCache, ProviderBillingStatus storedStatus)
    {
        if (metrics.Length == 0) return EmptyMetricState(availability, status);
        var descriptions = new List<string>();
        var quota = metrics.FirstOrDefault(metric => metric.Kind == ProviderMetricKind.QuotaLimit &&
            metric.Scope == ProviderMetricScope.ApiKey &&
            metric.Unit is { Kind: ProviderMetricUnitKind.ProviderSpecific, Code: "quota-units" });
        var primary = metrics.FirstOrDefault(metric => metric.Kind == ProviderMetricKind.Spending);
        if (quota is { IsUnlimited: true })
        {
            descriptions.Add("API key limit unlimited");
        }
        else if (quota is not null)
        {
            var remaining = metrics.FirstOrDefault(metric => metric.Kind == ProviderMetricKind.QuotaRemaining &&
                AreComparable(quota, metric) && metric.ObservedAt == quota.ObservedAt);
            if (remaining is not null) descriptions.Add(FormatMetricValue(remaining, "API key remaining"));
            descriptions.Add(FormatMetricValue(quota, "API key maximum"));
        }
        else if (primary is not null)
        {
            descriptions.Add(FormatMetricValue(primary, $"{ScopeLabel(primary.Scope)} spending"));
            var limit = metrics.FirstOrDefault(metric => metric.Kind == ProviderMetricKind.QuotaLimit && AreComparable(primary, metric));
            var remaining = metrics.FirstOrDefault(metric => metric.Kind == ProviderMetricKind.QuotaRemaining && AreComparable(primary, metric));
            if (limit is not null) descriptions.Add(FormatMetricValue(limit, "allowance"));
            if (remaining is not null) descriptions.Add(FormatMetricValue(remaining, "remaining allowance"));
        }
        else
        {
            foreach (var metric in metrics.OrderBy(MetricPriority).Take(2))
                descriptions.Add(FormatMetricValue(metric, MetricLabel(metric.Kind)));
        }

        var body = string.Join(" · ", descriptions);
        return AddMetricFreshness(body, status, fromCache, storedStatus, metrics.Any(metric => metric.Availability == ProviderMetricAvailability.Cached));
    }

    private static string AddMetricFreshness(string body, ProviderBillingStatus status, bool fromCache,
        ProviderBillingStatus storedStatus, bool hasRetainedMetric)
    {
        if (status is ProviderBillingStatus.Unauthorized or ProviderBillingStatus.AuthRejected or
            ProviderBillingStatus.Unavailable or ProviderBillingStatus.InvalidResponse)
            return $"{StatusLabel(status)} · last known {body}";
        if (status == ProviderBillingStatus.Stale)
            return $"Stale · last known {body}";
        if (fromCache || status == ProviderBillingStatus.Cached)
            return $"{body} · cached";
        if (status == ProviderBillingStatus.Partial || storedStatus == ProviderBillingStatus.Partial || hasRetainedMetric)
            return hasRetainedMetric ? $"Partial · {body}" : $"{body} · partial";
        return body;
    }

    private static string EmptyMetricState(ProviderMetricAvailability availability, ProviderBillingStatus overallStatus)
    {
        if (availability == ProviderMetricAvailability.Unknown && overallStatus is
            ProviderBillingStatus.Unauthorized or ProviderBillingStatus.AuthRejected or ProviderBillingStatus.Unavailable or ProviderBillingStatus.InvalidResponse)
            return StatusLabel(overallStatus);

        return availability switch
        {
            ProviderMetricAvailability.Unsupported => "Not supported",
            ProviderMetricAvailability.Unauthorized => "Unauthorized",
            ProviderMetricAvailability.Unavailable => "Unavailable",
            ProviderMetricAvailability.InvalidResponse => "Invalid response",
            ProviderMetricAvailability.Partial => "Partial · unknown",
            ProviderMetricAvailability.Cached => "Unknown · cached",
            ProviderMetricAvailability.Stale => "Stale · unknown",
            _ => "Unknown",
        };
    }

    private static string FormatMetricValue(ProviderMetric metric, string? label = null)
    {
        if (metric is { IsUnlimited: true, Value: null }) return "API key limit unlimited";
        if (metric.Scope == ProviderMetricScope.ApiKey &&
            ProviderQuotaDisplayFormatter.Format(metric.Display, System.Globalization.CultureInfo.InvariantCulture) is { } converted)
        {
            return string.IsNullOrWhiteSpace(label) ? converted : $"{label} {converted}";
        }

        var value = metric.Value!.Value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture);
        var unit = metric.Unit.Kind switch
        {
            ProviderMetricUnitKind.Currency => metric.Unit.Code ?? "currency unknown",
            ProviderMetricUnitKind.ProviderSpecific => metric.Unit.Code == "quota-units" ? "quota units" : "provider units",
            ProviderMetricUnitKind.Requests => "requests",
            ProviderMetricUnitKind.Tokens => "tokens",
            _ => "unit unknown",
        };
        return string.IsNullOrWhiteSpace(label) ? $"{value} {unit}" : $"{label} {value} {unit}";
    }

    private static string BalanceScopeLabel(IReadOnlyList<ProviderMetric> metrics)
    {
        var scopes = metrics.Select(metric => metric.Scope).Distinct().ToArray();
        return scopes.Length == 1 ? scopes[0] switch
        {
            ProviderMetricScope.ApiKey => "API key balance:",
            ProviderMetricScope.Organization => "Organization balance:",
            ProviderMetricScope.Workspace => "Workspace balance:",
            ProviderMetricScope.Team => "Team balance:",
            ProviderMetricScope.Subscription => "Subscription balance:",
            ProviderMetricScope.Account => "Account balance:",
            _ => "Balance:",
        } : "Balance:";
    }

    private static string ScopeLabel(ProviderMetricScope scope) => scope switch
    {
        ProviderMetricScope.ApiKey => "API key",
        ProviderMetricScope.Account => "Account",
        ProviderMetricScope.Organization => "Organization",
        ProviderMetricScope.Workspace => "Workspace",
        ProviderMetricScope.Team => "Team",
        ProviderMetricScope.Subscription => "Subscription",
        _ => "Provider",
    };

    private static string MetricLabel(ProviderMetricKind kind) => kind switch
    {
        ProviderMetricKind.QuotaLimit => "Quota limit",
        ProviderMetricKind.QuotaRemaining => "Remaining allowance",
        ProviderMetricKind.ProviderCredits => "Provider credits",
        ProviderMetricKind.ProviderSpecificUsage => "Usage",
        ProviderMetricKind.RequestCount => "Requests used",
        ProviderMetricKind.RequestAllowance => "Request allowance",
        ProviderMetricKind.TokenCount => "Tokens used",
        ProviderMetricKind.TokenAllowance => "Token allowance",
        ProviderMetricKind.AllocatedCredits => "Allocated credits",
        ProviderMetricKind.PurchasedCredits => "Purchased credits",
        _ => "Usage",
    };

    private static int MetricPriority(ProviderMetric metric) => metric.Kind switch
    {
        ProviderMetricKind.ProviderSpecificUsage => 0,
        ProviderMetricKind.ProviderCredits => 1,
        ProviderMetricKind.QuotaRemaining => 2,
        ProviderMetricKind.RequestCount => 3,
        ProviderMetricKind.TokenCount => 4,
        ProviderMetricKind.QuotaLimit => 5,
        ProviderMetricKind.RequestAllowance => 6,
        ProviderMetricKind.TokenAllowance => 7,
        _ => 8,
    };

    private static bool AreComparable(ProviderMetric left, ProviderMetric right) =>
        left.Scope == right.Scope && ProviderMetricNormalizer.UnitsEqual(left.Unit, right.Unit) &&
        string.Equals(left.Period, right.Period, StringComparison.Ordinal) &&
        left.PeriodSemantics == right.PeriodSemantics &&
        left.PeriodStartAt == right.PeriodStartAt && left.PeriodEndAt == right.PeriodEndAt &&
        left.ResetAt == right.ResetAt;

    private static bool IsUsageMetric(ProviderMetricKind kind) => kind is
        ProviderMetricKind.Spending or ProviderMetricKind.QuotaLimit or ProviderMetricKind.QuotaRemaining or
        ProviderMetricKind.ProviderCredits or ProviderMetricKind.ProviderSpecificUsage or ProviderMetricKind.RequestCount or
        ProviderMetricKind.RequestAllowance or ProviderMetricKind.TokenCount or ProviderMetricKind.TokenAllowance;

    private static string StatusLabel(ProviderBillingStatus status) => status switch
    {
        ProviderBillingStatus.Live => "Live",
        ProviderBillingStatus.Partial => "Partial",
        ProviderBillingStatus.Cached => "Cached",
        ProviderBillingStatus.Stale => "Stale",
        ProviderBillingStatus.Unsupported => "Not supported",
        ProviderBillingStatus.AuthRejected or ProviderBillingStatus.Unauthorized => "Unauthorized",
        ProviderBillingStatus.Unavailable => "Unavailable",
        ProviderBillingStatus.InvalidResponse => "Invalid response",
        _ => "Unknown",
    };

    public void SetDiscoveredModels(IReadOnlyList<string> models)
    {
        if (models.Count > 0 && ModelsReportedCount == 0)
        {
            ModelsReportedCount = models.Count;
            ModelsStatusMessage = $"{models.Count} models available";
        }
    }

    /// <summary>
    /// Applies the one authoritative refresh result. A newer service generation
    /// is the only state allowed to replace a card's previous error text.
    /// </summary>
    public void ApplyRefreshResult(ModelInventoryRefreshResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.RequestGeneration < _latestRefreshGeneration)
        {
            return;
        }

        if (result.RequestGeneration > _latestRefreshGeneration)
        {
            _latestRefreshGeneration = result.RequestGeneration;
            _latestRefreshIsTerminal = false;
        }

        // A delayed progress notification from this same request must not put
        // the card back into its loading state after a terminal result.
        if (_latestRefreshIsTerminal && result.Outcome == ModelInventoryRefreshOutcome.Refreshing)
        {
            return;
        }

        switch (result.Outcome)
        {
            case ModelInventoryRefreshOutcome.Refreshing:
                IsLoadingModels = true;
                ModelsStatusMessage = result.ProgressMessage ?? "Refreshing models...";
                break;
            case ModelInventoryRefreshOutcome.Succeeded:
                _latestRefreshIsTerminal = true;
                IsLoadingModels = false;
                ModelsReportedCount = result.ModelsReportedCount;
                ModelsEnabledCount = Profile.ModelInventory?.GetEnabledModels().Count() ?? 0;
                LastModelsRefreshAt = result.ObservedAt;
                ModelsStatusMessage = $"{result.ModelsReportedCount} models reported · Updated just now";
                break;
            case ModelInventoryRefreshOutcome.Superseded:
                // The latest generation will publish its own projection. Do not
                // replace a newer success with a stale terminal observation.
                break;
            default:
                _latestRefreshIsTerminal = true;
                IsLoadingModels = false;
                ModelsStatusMessage = result.ErrorMessageSanitized
                    ?? "Model refresh did not return a usable inventory.";
                break;
        }
    }

    public void ApplyRefreshFailure(long requestGeneration, string message)
    {
        ApplyRefreshResult(new ModelInventoryRefreshResult
        {
            ProfileId = Id,
            RequestGeneration = requestGeneration,
            Outcome = ModelInventoryRefreshOutcome.Failed,
            ErrorKind = "RefreshFailed",
            ErrorMessageSanitized = message,
            VerificationStatus = ModelCatalogVerificationStatus.Failed,
            ObservedAt = DateTimeOffset.UtcNow,
        });
    }

    private void RefreshInventoryPresentation(ApiProviderProfile profile)
    {
        var inventory = profile.ModelInventory;
        QuickSelectableModels.Clear();
        if (inventory is not null)
        {
            foreach (var model in inventory.Models.Where(model =>
                         model is not null &&
                         model.Enabled &&
                         model.Availability != ModelAvailability.NotReported &&
                         !string.IsNullOrWhiteSpace(model.Slug)))
            {
                QuickSelectableModels.Add(new ApiProviderQuickModelChoice(
                    model.Slug,
                    string.IsNullOrWhiteSpace(model.DisplayName)
                        ? ModelDisplayName.FromSlug(model.Slug)
                        : model.DisplayName!));
            }
        }

        OnPropertyChanged(nameof(HasSelectableModels));
        OnPropertyChanged(nameof(ShowDisplayedSelectedModel));
        OnPropertyChanged(nameof(DisplayedSelectedModel));
        OnPropertyChanged(nameof(CanSelectModel));
        ModelsEnabledCount = inventory?.GetEnabledModels().Count() ?? 0;
        ModelsReportedCount = inventory?.Models.Count(item => item.Availability == ModelAvailability.Reported)
            ?? profile.DiscoveredModels?.Count
            ?? 0;
        LastModelsRefreshAt = inventory?.LastDiscoveryAt;

        if (ModelsReportedCount > 0 && string.IsNullOrWhiteSpace(ModelsStatusMessage))
        {
            ModelsStatusMessage = $"{ModelsReportedCount} models reported";
        }
    }

    private void ApplyTruthfulCapabilityFacts(ProviderDescriptor? descriptor)
    {
        if (descriptor?.Capabilities?.Balance?.Status == CapabilityStatus.Unknown)
        {
            BalanceSummary = "Unknown";
        }
        else if (descriptor?.Capabilities?.Balance?.Status == CapabilityStatus.Unsupported)
        {
            BalanceSummary = "Not supported";
        }

        if (descriptor?.Capabilities?.Usage?.Status == CapabilityStatus.Unknown)
        {
            UsageSummary = "Unknown";
        }
        else if (descriptor?.Capabilities?.Usage?.Status == CapabilityStatus.Unsupported)
        {
            UsageSummary = "Not supported";
        }
    }

    public string RouteTooltip
    {
        get
        {
            if (Descriptor != null && !string.IsNullOrWhiteSpace(Profile.SelectedRouteId))
            {
                var route = Descriptor.Routes.FirstOrDefault(r => string.Equals(r.Id, Profile.SelectedRouteId, StringComparison.OrdinalIgnoreCase));
                if (route != null)
                {
                    var regionPart = !string.IsNullOrWhiteSpace(route.Region) ? $" ({route.Region})" : string.Empty;
                    return CanToggleRoute
                        ? $"Route: {route.DisplayName}{regionPart} - Click to cycle route"
                        : $"Route: {route.DisplayName}{regionPart}";
                }
            }
            return CanToggleRoute ? "Click to switch route" : "Route";
        }
    }

    private static string ResolveRouteName(ApiProviderProfile profile, ProviderDescriptor? descriptor)
    {
        if (descriptor != null && !string.IsNullOrWhiteSpace(profile.SelectedRouteId))
        {
            var route = descriptor.Routes.FirstOrDefault(r => string.Equals(r.Id, profile.SelectedRouteId, StringComparison.OrdinalIgnoreCase));
            if (route != null)
                return route.DisplayName;
        }

        if (descriptor != null && descriptor.Routes.Count > 0)
        {
            var defaultRoute = descriptor.Routes.FirstOrDefault(r => r.IsDefault) ?? descriptor.Routes[0];
            return defaultRoute.DisplayName;
        }

        return !string.IsNullOrWhiteSpace(profile.SelectedRouteId)
            ? profile.SelectedRouteId
            : "Primary";
    }

    private static string ComputeInitials(string providerName, string displayName)
    {
        var src = !string.IsNullOrWhiteSpace(providerName) ? providerName : displayName;
        if (src.StartsWith("Router.Cheap", StringComparison.OrdinalIgnoreCase)) return "RC";
        if (src.StartsWith("OpenRouter", StringComparison.OrdinalIgnoreCase)) return "OR";

        var parts = src.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return (char.ToUpperInvariant(parts[0][0]).ToString() + char.ToUpperInvariant(parts[1][0])).Trim();
        if (parts.Length == 1 && parts[0].Length >= 2)
            return parts[0][..2].ToUpperInvariant();
        return "AP";
    }
}
