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
    bool SupportsNicknameAuthority => false;

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

    Task<AuthorityDaemonInfo> GetAuthorityDaemonInfoAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Nickname authority discovery is not available on this client.");

    Task<NicknameAuthorityStatus> GetNicknameAuthorityAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Nickname authority operations are not available on this client.");

    Task<NicknameAuthorityStatus> BeginNicknameAuthorityPreparationAsync(long expectedEpoch, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Nickname authority operations are not available on this client.");

    Task<NicknameAuthorityStatus> MarkNicknameAuthorityVerifiedAsync(long expectedEpoch, string migrationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Nickname authority operations are not available on this client.");

    Task<NicknameAuthorityStatus> ActivateNicknameAuthorityAsync(long expectedEpoch, string migrationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Nickname authority operations are not available on this client.");

    Task<NicknameAuthorityStatus> BeginNicknameAuthorityRollbackAsync(long expectedEpoch, string migrationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Nickname authority operations are not available on this client.");

    Task<NicknameAuthorityStatus> CompleteNicknameAuthorityRollbackAsync(long expectedEpoch, string migrationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Nickname authority operations are not available on this client.");

    Task<AccountNicknameCollection> ListNicknamesAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Nickname authority operations are not available on this client.");

    Task<AccountNicknameState> GetNicknameAsync(string accountId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Nickname authority operations are not available on this client.");

    Task<NicknameMutationResult> UpdateNicknameAsync(AccountNicknameMutation mutation, long expectedAuthorityEpoch, string? migrationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Nickname authority operations are not available on this client.");

    Task<SourceReconcileResult> ReconcileSourceAsync(AccountSourceCollection collection, long expectedAuthorityEpoch, string? migrationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Nickname authority operations are not available on this client.");

    Task<int> GetDatabaseSchemaVersionAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Database schema discovery is not available on this client.");
}

internal interface IAccountStateShadowClientFactory
{
    bool IsLastConnectionHealthy { get; }

    Task<IAccountStateShadowClient> ConnectCompatibleAsync(CancellationToken cancellationToken);

    Task<IAccountStateShadowClient> ConnectNicknameAuthorityCompatibleAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Nickname authority daemon connections are not available on this factory.");

    Task<IAccountStateShadowClient?> TryConnectExistingNicknameAuthorityAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IAccountStateShadowClient?>(null);

    Task StopOwnedProcessAsync(CancellationToken cancellationToken);

    Task StopOwnedLegacyProcessAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
