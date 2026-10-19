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
    long DurationMilliseconds = 0);

internal sealed record AccountStateShadowReconcileCounts(int Inserted, int Updated, int Deleted, int Unchanged);

internal interface IAccountStateShadowClient : IAsyncDisposable
{
    Task<AccountStateShadowReconcileCounts> ReconcileAsync(
        CodexSwitcher.Core.AgentState.V1.AccountCollection collection,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CodexSwitcher.Core.AgentState.V1.AccountDescriptor>> ListAccountsAsync(CancellationToken cancellationToken);
}

internal interface IAccountStateShadowClientFactory
{
    Task<IAccountStateShadowClient> ConnectCompatibleAsync(CancellationToken cancellationToken);

    Task StopOwnedProcessAsync(CancellationToken cancellationToken);
}
