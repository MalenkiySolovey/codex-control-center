using CodexSwitcher.Core.AgentState.V1;

namespace CodexSwitcher.Core.Accounts.Contracts;

public enum AccountReadSource
{
    Local,
    DaemonCanary
}

public enum AccountReadCanaryState
{
    Off,
    ShadowDisabled,
    PendingInitialSync,
    DaemonActive,
    LocalFallbackDirty,
    LocalFallbackDaemonUnavailable,
    LocalFallbackMismatch,
    LocalFallbackProtocolIncompatible,
    LocalFallbackFailed
}

/// <summary>
/// A safe, canonical account projection and sanitized authority diagnostics.
/// It deliberately cannot represent full ProfileMetadata or credential state.
/// </summary>
public sealed record AccountReadSnapshot(
    AccountReadSource Source,
    long Generation,
    IReadOnlyList<AccountDescriptor> Accounts,
    int AccountCount,
    AccountReadCanaryState CanaryState,
    bool CanaryEnabled,
    long LastRequestedGeneration,
    long? LastMatchedGeneration,
    int MismatchCount,
    DateTimeOffset? LastTransitionAt,
    long TransitionDurationMilliseconds);

public interface IAccountReadSnapshotProvider
{
    AccountReadSnapshot Read();
}
