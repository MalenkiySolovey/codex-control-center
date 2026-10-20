namespace CodexSwitcher.Core.Accounts.Contracts;

/// <summary>Outcomes from the narrow interactive account nickname mutation path.</summary>
public enum AccountNicknameMutationOutcome
{
    LocalOnly,
    NoOp,
    DaemonAndLocal,
    Conflict,
    FailedCompensated,
    FailedDiverged
}

/// <summary>Contains only a stable outcome and sanitized category; never account data or revisions.</summary>
public sealed record AccountNicknameMutationResult(
    AccountNicknameMutationOutcome Outcome,
    string Category);

/// <summary>Orchestrates only user-initiated account nickname changes.</summary>
public interface IAccountNicknameMutationService
{
    Task<AccountNicknameMutationResult> RenameAsync(
        Guid accountId,
        string nickname,
        CancellationToken cancellationToken = default);
}
