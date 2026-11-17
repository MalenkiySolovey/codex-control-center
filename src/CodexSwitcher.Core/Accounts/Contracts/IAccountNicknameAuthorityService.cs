namespace CodexSwitcher.Core.Accounts.Contracts;

public sealed record AccountNicknameAuthorityConflict(Guid AccountId, string? LocalNickname, string? DaemonNickname);

public sealed record AccountNicknameImportResult(int ImportedAccounts, int AppliedNicknames, int FailedNicknames)
{
    public bool NicknamesFullyApplied => FailedNicknames == 0;
}

/// <summary>Coordinates the opt-in durable daemon ownership transition for UserNickname only.</summary>
public interface IAccountNicknameAuthorityService
{
    bool IsAuthorityPotentiallyActive { get; }
    bool IsAuthorityActive { get; }
    bool IsLocalAuthorityVerified { get; }
    IReadOnlyList<AccountNicknameAuthorityConflict> GetPendingConflicts();
    void PrepareStartupFence();
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<AccountNicknameMutationResult> RenameAsync(Guid accountId, string nickname, CancellationToken cancellationToken = default);
    Task<AccountNicknameMutationResult> ResolveMigrationConflictAsync(Guid accountId, string selectedNickname, CancellationToken cancellationToken = default);
    Task SynchronizeActiveSourceAsync(CancellationToken cancellationToken = default);
    Task<AccountNicknameImportResult> ImportAsync(string document, CancellationToken cancellationToken = default);
    Task<string> ExportOneAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<string> ExportAllAsync(CancellationToken cancellationToken = default);
    Task<bool> RollbackAsync(CancellationToken cancellationToken = default);
}
