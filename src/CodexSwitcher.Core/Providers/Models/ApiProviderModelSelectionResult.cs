using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Providers.Models;

public enum ApiProviderModelSelectionOutcome
{
    Selected = 1,
    NoOp = 2,
    Failed = 3,
    Superseded = 4,
}

/// <summary>
/// Outcome of selecting one exact enabled model from an API provider's
/// persisted inventory.
/// </summary>
public sealed record ApiProviderModelSelectionResult(
    Guid ProfileId,
    long RequestGeneration,
    string RequestedModel,
    ApiProviderModelSelectionOutcome Outcome,
    string Message,
    bool WasActiveTarget,
    bool Persisted,
    bool RuntimeVerified,
    bool RuntimeRestarted,
    TargetSwitchResult? SwitchResult = null);
