namespace CodexSwitcher.Infra.AgentState.Shadow;

/// <summary>Sanitized, non-persistent operational state; it contains no profile fields or paths.</summary>
public sealed record AccountStateShadowStatus(
    string Category,
    int AccountCount = 0,
    int MismatchCount = 0,
    int Inserted = 0,
    int Updated = 0,
    int Deleted = 0,
    int Unchanged = 0,
    long DurationMilliseconds = 0,
    long CurrentGeneration = 0,
    long LastRequestedGeneration = 0,
    long? LastMatchedGeneration = null,
    DateTimeOffset? LastTransitionAt = null);

public sealed record AccountStateShadowReadState(
    AccountStateShadowStatus Status,
    long? MatchedGeneration,
    IReadOnlyList<CodexSwitcher.Core.AgentState.V1.AccountDescriptor>? MatchedAccounts);

public interface IAccountStateShadowReadAuthority
{
    AccountStateShadowReadState ReadAuthorityState { get; }

    bool IsDaemonConnectionHealthy { get; }
}

internal sealed record AccountStateShadowReconcileCounts(int Inserted, int Updated, int Deleted, int Unchanged);

internal interface IAccountStateShadowClient : IAsyncDisposable
{
    bool SupportsNicknameMutation => false;

    Task<AccountStateShadowReconcileCounts> ReconcileAsync(
        CodexSwitcher.Core.AgentState.V1.AccountCollection collection,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CodexSwitcher.Core.AgentState.V1.AccountDescriptor>> ListAccountsAsync(CancellationToken cancellationToken);

    Task<CodexSwitcher.Core.AgentState.V1.RevisionedAccountDescriptor> GetAccountAsync(
        string accountId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Account metadata reads are not available on this client.");

    Task<CodexSwitcher.Core.AgentState.V1.RevisionedAccountDescriptor> UpdateMetadataAsync(
        CodexSwitcher.Core.AgentState.V1.AccountMetadataUpdate update,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Account metadata writes are not available on this client.");
}

internal interface IAccountStateShadowClientFactory
{
    bool IsLastConnectionHealthy { get; }

    Task<IAccountStateShadowClient> ConnectCompatibleAsync(CancellationToken cancellationToken);

    Task StopOwnedProcessAsync(CancellationToken cancellationToken);
}
