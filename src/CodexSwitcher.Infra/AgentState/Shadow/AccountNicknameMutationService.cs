using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.AgentState.V1;
using System.Text.Json;

namespace CodexSwitcher.Infra.AgentState.Shadow;

/// <summary>
/// Experimental daemon-first CAS path for interactive nickname edits only. The registry remains
/// the local owner; the daemon write is mirrored locally and reconciled back to the canonical
/// projection before the read-authority canary can serve it again.
/// </summary>
internal sealed class AccountNicknameMutationService : IAccountNicknameMutationService
{
    private readonly AccountRegistry _registry;
    private readonly AccountNicknameWriteCanarySettings _writeSettings;
    private readonly AccountStateShadowCoordinator _coordinator;
    private readonly IAccountStateShadowClientFactory _clientFactory;
    private readonly AccountNicknameMutationDiagnostics _diagnostics;

    public AccountNicknameMutationService(
        AccountRegistry registry,
        AccountNicknameWriteCanarySettings writeSettings,
        AccountStateShadowCoordinator coordinator,
        IAccountStateShadowClientFactory clientFactory,
        AccountNicknameMutationDiagnostics diagnostics)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _writeSettings = writeSettings ?? throw new ArgumentNullException(nameof(writeSettings));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public async Task<AccountNicknameMutationResult> RenameAsync(
        Guid accountId,
        string nickname,
        CancellationToken cancellationToken = default)
    {
        var desiredNickname = nickname?.Trim() ?? string.Empty;
        var initial = _registry.ReadCommittedSnapshot();
        var initialProfile = initial.Profiles.FirstOrDefault(profile => profile.Id == accountId);
        if (initialProfile is null || string.Equals(initialProfile.Nickname ?? string.Empty, desiredNickname, StringComparison.Ordinal))
            return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.NoOp, "unchanged_or_missing");

        if (!_writeSettings.Enabled)
            return CommitLocalOnly(accountId, desiredNickname, "canary_off");

        await using var gate = await _coordinator.AcquireNicknameMutationLeaseAsync(cancellationToken).ConfigureAwait(false);
        var preimage = _registry.ReadCommittedSnapshot();
        var profile = preimage.Profiles.FirstOrDefault(item => item.Id == accountId);
        if (profile is null || string.Equals(profile.Nickname ?? string.Empty, desiredNickname, StringComparison.Ordinal))
            return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.NoOp, "unchanged_or_missing");

        if (initial.Generation != preimage.Generation)
            return CommitLocalOnly(accountId, desiredNickname, "generation_changed_before_canary");

        var expectedDescriptor = ProjectProfile(preimage.Profiles, accountId);
        if (!CanMutateAtGeneration(preimage, expectedDescriptor))
            return CommitLocalOnly(accountId, desiredNickname, "read_authority_not_matched");

        IAccountStateShadowClient client;
        try
        {
            client = await _clientFactory.ConnectCompatibleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsTransportOrProtocolFailure(exception) || exception is AccountStateShadowRpcException)
        {
            return CommitLocalOnly(accountId, desiredNickname, ClassifyFailure(exception));
        }

        await using (client.ConfigureAwait(false))
        {
            if (!client.SupportsNicknameMutation)
                return CommitLocalOnly(accountId, desiredNickname, "capability_unavailable");

            var accountIdText = accountId.ToString("D");
            RevisionedAccountDescriptor current;
            try
            {
                current = await client.GetAccountAsync(accountIdText, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsTransportOrProtocolFailure(exception) || exception is AccountStateShadowRpcException)
            {
                return CommitLocalOnly(accountId, desiredNickname, ClassifyFailure(exception));
            }

            if (!string.Equals(current.Account.AccountId, accountIdText, StringComparison.Ordinal) ||
                !DescriptorsEqual(expectedDescriptor, current.Account) ||
                string.IsNullOrEmpty(current.Revision) || current.Revision.Length > 256)
            {
                return CommitLocalOnly(accountId, desiredNickname, "account_descriptor_mismatch");
            }

            if (!CanMutateAtGeneration(preimage, expectedDescriptor))
                return CommitLocalOnly(accountId, desiredNickname, "generation_changed_before_update");

            _diagnostics.Transition("Ready", "eligible", preimage.Generation);
            _diagnostics.Transition("UpdatingDaemon", "pending", preimage.Generation);

            var updated = await UpdateOrClassifyAsync(
                client,
                accountIdText,
                desiredNickname,
                current,
                expectedDescriptor,
                cancellationToken).ConfigureAwait(false);

            if (updated.Kind == UpdateKind.LocalOnly)
                return CommitLocalOnly(accountId, desiredNickname, updated.Category);

            if (updated.Kind == UpdateKind.Conflict)
            {
                _coordinator.MarkNicknameMutationPending(preimage.Generation, preimage.Profiles.Count);
                _coordinator.RequestReconcile();
                _diagnostics.Transition("Conflict", updated.Category, preimage.Generation);
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.Conflict, updated.Category);
            }

            if (updated.Kind == UpdateKind.Diverged || updated.Account is null)
            {
                _coordinator.MarkNicknameMutationPending(preimage.Generation, preimage.Profiles.Count);
                _coordinator.RequestReconcile();
                _diagnostics.Transition("FailedDiverged", updated.Category, preimage.Generation);
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.FailedDiverged, updated.Category);
            }

            // Invalidate the cached matched read before local persistence. If the mirror fails,
            // the provider must serve the local pre-image immediately, even at the same generation.
            _coordinator.MarkNicknameMutationPending(preimage.Generation, preimage.Profiles.Count);
            _diagnostics.Transition("MirroringLocal", "pending", preimage.Generation);

            try
            {
                if (_registry.TryRenameIfGeneration(accountId, desiredNickname, preimage.Generation))
                {
                    var mirrored = _registry.ReadCommittedSnapshot();
                    _diagnostics.WaitForConvergence(mirrored.Generation);
                    _coordinator.RequestReconcile();
                    return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.DaemonAndLocal, "ok");
                }
            }
            catch (Exception)
            {
                // AccountRegistry restores its full in-memory pre-image and generation on failed persistence.
            }

            var latest = _registry.ReadCommittedSnapshot();
            var latestProfile = latest.Profiles.FirstOrDefault(item => item.Id == accountId);
            if (latestProfile is not null && string.Equals(latestProfile.Nickname ?? string.Empty, desiredNickname, StringComparison.Ordinal))
            {
                _diagnostics.WaitForConvergence(latest.Generation);
                _coordinator.RequestReconcile();
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.DaemonAndLocal, "already_mirrored");
            }

            var compensation = await TryCompensateAsync(
                client,
                accountIdText,
                updated.Account,
                latestProfile,
                latest.Profiles).ConfigureAwait(false);
            if (compensation)
            {
                _diagnostics.Transition("FailedCompensated", "local_persistence_failed", latest.Generation);
                _diagnostics.WaitForConvergence(latest.Generation);
                _coordinator.RequestReconcile();
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.FailedCompensated, "local_persistence_failed");
            }

            // Keep local authority and ask the existing canonical reconciliation path to converge once.
            _diagnostics.Transition("FailedDiverged", "compensation_failed", latest.Generation);
            _coordinator.RequestReconcile();
            return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.FailedDiverged, "compensation_failed");
        }
    }

    private static async Task<UpdateClassification> UpdateOrClassifyAsync(
        IAccountStateShadowClient client,
        string accountId,
        string desiredNickname,
        RevisionedAccountDescriptor before,
        AccountDescriptor expectedDescriptor,
        CancellationToken cancellationToken)
    {
        RevisionedAccountDescriptor? result = null;
        AccountStateShadowRpcException? rpcFailure = null;
        Exception? transportFailure = null;
        try
        {
            result = await client.UpdateMetadataAsync(
                new AccountMetadataUpdate(1, accountId, desiredNickname, before.Revision),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var afterCancel = await TryReadAccountAsync(client, accountId).ConfigureAwait(false);
            if (IsDesiredAfterUpdate(afterCancel, expectedDescriptor, accountId, desiredNickname, before.Revision))
                result = afterCancel;
            else if (IsUnchanged(afterCancel, before))
                throw;
            else
                return new UpdateClassification(UpdateKind.Diverged, null, "update_canceled_after_send");
        }
        catch (AccountStateShadowRpcException exception)
        {
            rpcFailure = exception;
        }
        catch (Exception exception) when (IsTransportOrProtocolFailure(exception))
        {
            // A broken pipe or timeout after write is ambiguous; only read-back can establish outcome.
            transportFailure = exception;
        }

        if (rpcFailure?.Category is "method_not_found" or "validation_failed" or "not_found" or "unsupported_schema_version")
            return new UpdateClassification(UpdateKind.LocalOnly, null, rpcFailure.Category);

        if (result is null)
        {
            var observed = await TryReadAccountAsync(client, accountId).ConfigureAwait(false);
            if (IsDesiredAfterUpdate(observed, expectedDescriptor, accountId, desiredNickname, before.Revision))
                result = observed;
            else if (rpcFailure?.Category == "revision_conflict")
                return new UpdateClassification(UpdateKind.Conflict, null,
                    IsSameAccount(observed, accountId) ? "revision_conflict_different_name" : "revision_conflict_unverified");
            else if (IsUnchanged(observed, before))
                return new UpdateClassification(UpdateKind.LocalOnly, null,
                    rpcFailure?.Category ?? (transportFailure is null ? "update_not_applied" : ClassifyFailure(transportFailure)));
            else
                return new UpdateClassification(UpdateKind.Diverged, null,
                    rpcFailure?.Category ?? (transportFailure is null ? "update_outcome_unverified" : ClassifyFailure(transportFailure)));
        }

        if (IsDesiredAfterUpdate(result, expectedDescriptor, accountId, desiredNickname, before.Revision))
            return new UpdateClassification(UpdateKind.Applied, result, rpcFailure?.Category ?? "ok");

        // A malformed/mismatched success response may follow a committed mutation. Read back once,
        // then either accept the verified target, prove no write occurred, or force local fallback.
        var verified = await TryReadAccountAsync(client, accountId).ConfigureAwait(false);
        if (IsDesiredAfterUpdate(verified, expectedDescriptor, accountId, desiredNickname, before.Revision))
            return new UpdateClassification(UpdateKind.Applied, verified, "verified_by_readback");
        if (IsUnchanged(verified, before))
            return new UpdateClassification(UpdateKind.LocalOnly, null, "malformed_response_not_applied");
        return new UpdateClassification(UpdateKind.Diverged, null, "malformed_response_unverified");
    }

    private static async Task<bool> TryCompensateAsync(
        IAccountStateShadowClient client,
        string accountId,
        RevisionedAccountDescriptor applied,
        ProfileMetadata? latestProfile,
        IReadOnlyList<ProfileMetadata> latestProfiles)
    {
        if (latestProfile is null)
            return false;

        var restoreNickname = latestProfile.Nickname ?? string.Empty;
        var expectedLocal = ProjectProfile(latestProfiles, latestProfile.Id);
        try
        {
            var restored = await client.UpdateMetadataAsync(
                new AccountMetadataUpdate(1, accountId, restoreNickname, applied.Revision),
                CancellationToken.None).ConfigureAwait(false);
            return string.Equals(restored.Account.AccountId, accountId, StringComparison.Ordinal) &&
                DescriptorsEqual(expectedLocal, restored.Account) &&
                !string.Equals(restored.Revision, applied.Revision, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<RevisionedAccountDescriptor?> TryReadAccountAsync(IAccountStateShadowClient client, string accountId)
    {
        try
        {
            return await client.GetAccountAsync(accountId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool CanMutateAtGeneration(AccountRegistrySnapshot snapshot, AccountDescriptor expectedDescriptor)
    {
        var readState = _coordinator.ReadAuthorityState;
        var status = readState.Status;
        return _writeSettings.Enabled &&
            string.Equals(status.Category, "synchronized", StringComparison.Ordinal) &&
            readState.MatchedGeneration == snapshot.Generation &&
            status.LastMatchedGeneration == snapshot.Generation &&
            readState.MatchedAccounts is not null &&
            _coordinator.IsDaemonConnectionHealthy &&
            _registry.CurrentGeneration == snapshot.Generation &&
            AccountStateShadowCoordinator.CountMismatches([expectedDescriptor],
                readState.MatchedAccounts.Where(account => account.AccountId == expectedDescriptor.AccountId).ToArray()) == 0;
    }

    private static AccountDescriptor ProjectProfile(IReadOnlyList<ProfileMetadata> profiles, Guid accountId) =>
        AccountStateShadowCoordinator.ProjectProfiles(profiles)
            .Single(account => string.Equals(account.AccountId, accountId.ToString("D"), StringComparison.Ordinal));

    private static bool DescriptorsEqual(AccountDescriptor expected, AccountDescriptor actual) =>
        AccountStateShadowCoordinator.CountMismatches([expected], [actual]) == 0;

    private static bool IsDesiredAfterUpdate(
        RevisionedAccountDescriptor? candidate,
        AccountDescriptor expectedBefore,
        string accountId,
        string desiredNickname,
        string previousRevision) =>
        candidate is not null &&
        string.Equals(candidate.Account.AccountId, accountId, StringComparison.Ordinal) &&
        string.Equals(candidate.Account.DisplayName, desiredNickname, StringComparison.Ordinal) &&
        DescriptorsEqual(expectedBefore with { DisplayName = desiredNickname }, candidate.Account) &&
        !string.IsNullOrEmpty(candidate.Revision) &&
        candidate.Revision.Length <= 256 &&
        !string.Equals(candidate.Revision, previousRevision, StringComparison.Ordinal);

    private static bool IsUnchanged(RevisionedAccountDescriptor? candidate, RevisionedAccountDescriptor before) =>
        candidate is not null &&
        string.Equals(candidate.Revision, before.Revision, StringComparison.Ordinal) &&
        DescriptorsEqual(before.Account, candidate.Account);

    private static bool IsSameAccount(RevisionedAccountDescriptor? candidate, string accountId) =>
        candidate is not null && string.Equals(candidate.Account.AccountId, accountId, StringComparison.Ordinal);

    private static bool IsTransportOrProtocolFailure(Exception exception) =>
        exception is IOException or TimeoutException or UnauthorizedAccessException or JsonException or
            AccountStateShadowCompatibilityException or AccountStateShadowUnavailableException;

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        AccountStateShadowRpcException { Category: { } category } => category,
        AccountStateShadowRpcTimeoutException => "timeout",
        EndOfStreamException => "pipe_closed",
        IOException => "pipe_unavailable",
        UnauthorizedAccessException => "pipe_access_denied",
        AccountStateShadowCompatibilityException or JsonException => "malformed_or_protocol_incompatible",
        AccountStateShadowUnavailableException => "daemon_unavailable",
        TimeoutException => "timeout",
        _ => "request_failed"
    };

    private AccountNicknameMutationResult CommitLocalOnly(Guid accountId, string nickname, string category)
    {
        _registry.Rename(accountId, nickname);
        _diagnostics.Transition("LocalOnly", category, _registry.CurrentGeneration);
        return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.LocalOnly, category);
    }

    private enum UpdateKind
    {
        Applied,
        LocalOnly,
        Conflict,
        Diverged
    }

    private sealed record UpdateClassification(UpdateKind Kind, RevisionedAccountDescriptor? Account, string Category);
}
