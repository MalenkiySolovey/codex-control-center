using System.Globalization;
using System.Text.Json;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.AgentState.V1;
using CodexSwitcher.Infra.Common.Paths;

namespace CodexSwitcher.Infra.AgentState.Shadow;

/// <summary>Owns the opt-in UserNickname authority transition while leaving every other account fact local.</summary>
internal sealed class AccountNicknameAuthorityService : IAccountNicknameAuthorityService, IDisposable
{
    private const int MaximumReplans = 3;
    private readonly AccountRegistry _registry;
    private readonly AccountTransferService _transfer;
    private readonly NicknameAuthorityCanarySettings _settings;
    private readonly NicknameAuthorityMigrationJournal _journal;
    private readonly AccountStateShadowCoordinator _coordinator;
    private readonly IAccountStateShadowClientFactory _clientFactory;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateSync = new();
    private NicknameAuthorityJournal? _journalState;
    private NicknameAuthorityStatus? _authorityState;
    private List<AccountNicknameAuthorityConflict> _pendingConflicts = [];
    private bool _startupFenceLoaded;
    private bool _journalInvalid;
    private bool _authorityMayBeActive;
    private bool _localAuthorityVerified = true;
    private int _mirrorUpdateInProgress;
    private int _activeSourceRefreshQueued;

    internal AccountNicknameAuthorityService(
        AccountRegistry registry,
        AccountTransferService transfer,
        NicknameAuthorityCanarySettings settings,
        NicknameAuthorityMigrationJournal journal,
        AccountStateShadowCoordinator coordinator,
        IAccountStateShadowClientFactory clientFactory,
        AppPaths paths)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _registry.SnapshotChanged += OnRegistrySnapshotChanged;
    }

    public bool IsAuthorityPotentiallyActive
    {
        get
        {
            lock (_stateSync)
                return _authorityMayBeActive || _journalInvalid || _registry.NicknameWritesFenced ||
                    NicknameAuthorityStates.IsAuthorityPossible(_authorityState?.State);
        }
    }

    public bool IsAuthorityActive
    {
        get
        {
            lock (_stateSync)
                return string.Equals(_authorityState?.State, NicknameAuthorityStates.DaemonNicknameAuthority, StringComparison.Ordinal);
        }
    }

    public bool IsLocalAuthorityVerified
    {
        get
        {
            lock (_stateSync)
                return _localAuthorityVerified && !_registry.NicknameWritesFenced &&
                    string.Equals(_authorityState?.State ?? NicknameAuthorityStates.LegacyLocal, NicknameAuthorityStates.LegacyLocal, StringComparison.Ordinal);
        }
    }

    public IReadOnlyList<AccountNicknameAuthorityConflict> GetPendingConflicts()
    {
        lock (_stateSync)
            return _pendingConflicts.ToArray();
    }

    public void PrepareStartupFence()
    {
        lock (_stateSync)
        {
            if (_startupFenceLoaded)
                return;

            try
            {
                _journalState = _journal.Read();
                _authorityMayBeActive = _journalState is not null &&
                    (NicknameAuthorityStates.IsAuthorityPossible(_journalState.AuthorityState) || _journalState.AuthorityWasActivated);
            }
            catch (Exception)
            {
                _journalInvalid = true;
                _authorityMayBeActive = true;
                _localAuthorityVerified = false;
            }

            if (_settings.Requested || _journalState is not null || _journalInvalid)
                _coordinator.DisableLegacyShadowWrites();
            if (_authorityMayBeActive || _journalInvalid)
            {
                _registry.SetNicknameWritesFenced(true);
                _localAuthorityVerified = false;
            }
            _startupFenceLoaded = true;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PrepareStartupFence();
            if (!_registry.IsLoaded || _journalInvalid)
                return;

            await using var profileLease = await ProfileMigrationLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false);
            // Another Control Center may have completed or advanced the durable lifecycle while
            // this process waited for the cross-process lease. Re-read the journal only after
            // owning the lease; the daemon status is re-read below before any state transition.
            var journal = ReadJournalSafely();
            if (_journalInvalid)
            {
                SetFailClosed("migration_journal_invalid");
                return;
            }

            var shouldDriveAuthority = _settings.Enabled || journal is not null;
            if (!_settings.CanStartDaemon && shouldDriveAuthority)
            {
                SetFailClosed("daemon_configuration_unavailable");
                return;
            }

            IAccountStateShadowClient? client = null;
            try
            {
                if (shouldDriveAuthority)
                {
                    _coordinator.DisableLegacyShadowWrites();
                    await _coordinator.PauseLegacyShadowAsync(cancellationToken).ConfigureAwait(false);
                    client = await _clientFactory.ConnectNicknameAuthorityCompatibleAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    client = await _clientFactory.TryConnectExistingNicknameAuthorityAsync(cancellationToken).ConfigureAwait(false);
                }

                if (client is null)
                {
                    if (_authorityMayBeActive || journal is not null)
                        SetFailClosed("daemon_unavailable");
                    else
                        SetLocalAuthority(NicknameAuthorityStates.LegacyLocal, verified: true);
                    return;
                }

                await using (client.ConfigureAwait(false))
                {
                    var (info, status) = await ReadAuthorityStateAsync(client, cancellationToken).ConfigureAwait(false);
                    SetAuthorityState(status);

                    switch (status.State)
                    {
                        case NicknameAuthorityStates.LegacyLocal:
                            await HandleLegacyLocalAsync(client, info, status, journal, cancellationToken).ConfigureAwait(false);
                            break;
                        case NicknameAuthorityStates.Preparing:
                            SetFailClosedState(status);
                            await EnsurePreparingJournalAsync(status, cancellationToken).ConfigureAwait(false);
                            await ContinuePreparationAsync(client, status, cancellationToken).ConfigureAwait(false);
                            break;
                        case NicknameAuthorityStates.Verified:
                            SetFailClosedState(status);
                            await EnsurePreparingJournalAsync(status, cancellationToken).ConfigureAwait(false);
                            await ActivateVerifiedMigrationAsync(client, status, cancellationToken).ConfigureAwait(false);
                            break;
                        case NicknameAuthorityStates.DaemonNicknameAuthority:
                            SetFailClosedState(status);
                            await EnsureActiveJournalAsync(status, cancellationToken).ConfigureAwait(false);
                            await RefreshActiveCacheAndSourceAsync(client, status, cancellationToken).ConfigureAwait(false);
                            break;
                        case NicknameAuthorityStates.RollbackRequired:
                            SetFailClosedState(status);
                            await EnsureActiveJournalAsync(status, cancellationToken).ConfigureAwait(false);
                            await CompleteRollbackAsync(client, status, cancellationToken).ConfigureAwait(false);
                            break;
                        default:
                            SetFailClosed("unknown_authority_state");
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (_authorityMayBeActive)
                    SetFailClosed("operation_canceled");
                throw;
            }
            catch (Exception exception)
            {
                var category = ClassifyFailure(exception);
                if (_authorityMayBeActive || _journalInvalid || _settings.Requested && _journalState?.AuthorityState == NicknameAuthorityStates.Preparing)
                    SetFailClosed(category);
                else
                    SetLocalAuthority(NicknameAuthorityStates.LegacyLocal, verified: false);
            }
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private async Task HandleLegacyLocalAsync(
        IAccountStateShadowClient client,
        AuthorityDaemonInfo info,
        NicknameAuthorityStatus status,
        NicknameAuthorityJournal? journal,
        CancellationToken cancellationToken)
    {
        if (journal?.AuthorityWasActivated == true)
        {
            if (!journal.RollbackCompleted || info.DatabaseSchemaVersion != 2 || journal.MigrationId is null ||
                !string.Equals(journal.MigrationId, status.MigrationId, StringComparison.Ordinal))
            {
                SetFailClosed("rollback_not_verified");
                return;
            }

            SetLocalAuthority(NicknameAuthorityStates.LegacyLocal, verified: true);
            return;
        }

        if (journal is not null)
        {
            if (info.DatabaseSchemaVersion != 1)
            {
                SetFailClosed("legacy_database_version_mismatch");
                return;
            }
            _journal.Delete();
            lock (_stateSync)
                _journalState = null;
        }

        if (!_settings.Requested)
        {
            SetLocalAuthority(NicknameAuthorityStates.LegacyLocal, verified: true);
            return;
        }

        if (!_settings.Enabled || info.DatabaseSchemaVersion != 1 || !_registry.IsLoaded)
        {
            SetLocalAuthority(NicknameAuthorityStates.LegacyLocal, verified: false);
            return;
        }

        await BeginPreparationAsync(client, status, cancellationToken).ConfigureAwait(false);
    }

    private async Task BeginPreparationAsync(IAccountStateShadowClient client, NicknameAuthorityStatus status, CancellationToken cancellationToken)
    {
        var snapshot = _registry.ReadCommittedSnapshot();
        SaveJournal(new NicknameAuthorityJournal
        {
            AuthorityState = NicknameAuthorityStates.LegacyLocal,
            AuthorityEpoch = status.Epoch,
            SourceGeneration = snapshot.Generation,
            Rows = snapshot.Profiles.Select(profile => new NicknameAuthorityMigrationRow
            {
                AccountId = profile.Id.ToString("D", CultureInfo.InvariantCulture),
                Phase = "preflight"
            }).ToArray()
        });

        // This durable fence makes an ambiguous beginPreparation outcome fail closed after a crash.
        SaveJournal(ReadJournalSafely()! with { AuthorityState = NicknameAuthorityStates.Preparing });
        var preparing = await client.BeginNicknameAuthorityPreparationAsync(status.Epoch, cancellationToken).ConfigureAwait(false);
        if (preparing.State != NicknameAuthorityStates.Preparing || preparing.Epoch <= status.Epoch ||
            string.IsNullOrWhiteSpace(preparing.MigrationId) || preparing.ProtocolManifestSha256 != AccountStateShadowRpcClient.ProtocolManifestSha256)
            throw new AccountStateShadowCompatibilityException();

        SetFailClosedState(preparing);
        SaveJournal(ReadJournalSafely()! with
        {
            AuthorityState = NicknameAuthorityStates.Preparing,
            AuthorityEpoch = preparing.Epoch,
            MigrationId = preparing.MigrationId,
            SourceGeneration = snapshot.Generation,
            Rows = snapshot.Profiles.Select(profile => new NicknameAuthorityMigrationRow
            {
                AccountId = profile.Id.ToString("D", CultureInfo.InvariantCulture),
                Phase = "pending"
            }).ToArray()
        });
        await ContinuePreparationAsync(client, preparing, cancellationToken).ConfigureAwait(false);
    }

    private async Task ContinuePreparationAsync(IAccountStateShadowClient client, NicknameAuthorityStatus initialStatus, CancellationToken cancellationToken)
    {
        if (initialStatus.State != NicknameAuthorityStates.Preparing || string.IsNullOrWhiteSpace(initialStatus.MigrationId))
            throw new AccountStateShadowCompatibilityException();

        lock (_stateSync)
            _pendingConflicts = [];

        for (var attempt = 0; attempt < MaximumReplans; attempt++)
        {
            var snapshot = _registry.ReadCommittedSnapshot();
            var beforeReconcile = await ReadNicknamesAsync(client, snapshot.Profiles, allowMissing: true, cancellationToken).ConfigureAwait(false);
            var source = BuildSourceCollection(snapshot.Profiles, beforeReconcile, migrationIntents: null, legacyNicknamesAreRelevant: true);
            await client.ReconcileSourceAsync(source, initialStatus.Epoch, initialStatus.MigrationId, cancellationToken).ConfigureAwait(false);

            var currentNicknames = await ReadNicknamesAsync(client, snapshot.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
            var conflicts = new List<AccountNicknameAuthorityConflict>();
            foreach (var profile in snapshot.Profiles)
            {
                var localNickname = NormalizeNickname(profile.Nickname);
                if (!currentNicknames.TryGetValue(profile.Id, out var daemonNickname))
                    throw new AccountStateShadowCompatibilityException();
                if (localNickname is not null &&
                    !string.Equals(localNickname, daemonNickname.Nickname, StringComparison.Ordinal))
                {
                    if (daemonNickname.Nickname is not null)
                    {
                        conflicts.Add(new AccountNicknameAuthorityConflict(profile.Id, localNickname, daemonNickname.Nickname));
                        continue;
                    }
                }

                if (localNickname is not null && daemonNickname.Nickname is null)
                {
                    var result = await TryApplyNicknameAsync(client, profile, localNickname, daemonNickname, initialStatus, migrationId: initialStatus.MigrationId, cancellationToken).ConfigureAwait(false);
                    if (result.Conflict)
                    {
                        conflicts.Add(new AccountNicknameAuthorityConflict(profile.Id, localNickname, result.Current?.Nickname));
                        continue;
                    }
                }
            }

            if (conflicts.Count > 0)
            {
                lock (_stateSync)
                    _pendingConflicts = conflicts;
                SaveRows(snapshot, currentNicknames, "conflict");
                SetFailClosed("nickname_conflict_pending");
                return;
            }

            if (_registry.CurrentGeneration != snapshot.Generation)
            {
                SaveRows(snapshot, currentNicknames, "replan_pending");
                continue;
            }

            var latestNicknames = await ReadNicknamesAsync(client, snapshot.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
            var mirrorMap = latestNicknames.ToDictionary(pair => pair.Key, pair => pair.Value.Nickname);
            if (!ApplyAndVerifyMirrors(snapshot.Generation, mirrorMap))
            {
                SetFailClosed("local_mirror_persistence_failed");
                return;
            }

            var verifiedSnapshot = _registry.ReadCommittedSnapshot();
            if (verifiedSnapshot.Profiles.Count != snapshot.Profiles.Count ||
                verifiedSnapshot.Profiles.Any(profile => !mirrorMap.ContainsKey(profile.Id)) ||
                !_registry.VerifyPersistedNicknameMirrors(mirrorMap))
            {
                SetFailClosed("local_mirror_readback_mismatch");
                return;
            }

            var refreshedNicknames = await ReadNicknamesAsync(client, verifiedSnapshot.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
            var finalSource = BuildSourceCollection(verifiedSnapshot.Profiles, refreshedNicknames, migrationIntents: null, legacyNicknamesAreRelevant: false);
            await client.ReconcileSourceAsync(finalSource, initialStatus.Epoch, initialStatus.MigrationId, cancellationToken).ConfigureAwait(false);
            var finalNicknames = await ReadNicknamesAsync(client, verifiedSnapshot.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
            var finalAccounts = await client.ListAccountsAsync(cancellationToken).ConfigureAwait(false);

            if (_registry.CurrentGeneration != verifiedSnapshot.Generation ||
                !VerifyEffectiveAccountSet(verifiedSnapshot.Profiles, finalAccounts, finalNicknames) ||
                !NicknamesEqual(refreshedNicknames, finalNicknames) ||
                !_registry.VerifyPersistedNicknameMirrors(mirrorMap))
            {
                SaveRows(verifiedSnapshot, finalNicknames, "replan_pending");
                continue;
            }

            SaveRows(verifiedSnapshot, finalNicknames, "verified_pending");
            var currentStatus = await client.GetNicknameAuthorityAsync(cancellationToken).ConfigureAwait(false);
            if (currentStatus.State != NicknameAuthorityStates.Preparing || currentStatus.Epoch != initialStatus.Epoch ||
                !string.Equals(currentStatus.MigrationId, initialStatus.MigrationId, StringComparison.Ordinal))
            {
                SetFailClosed("authority_state_changed");
                return;
            }

            var verified = await client.MarkNicknameAuthorityVerifiedAsync(currentStatus.Epoch, initialStatus.MigrationId, cancellationToken).ConfigureAwait(false);
            if (verified.State != NicknameAuthorityStates.Verified || verified.Epoch <= currentStatus.Epoch ||
                verified.MigrationId != initialStatus.MigrationId || verified.ProtocolManifestSha256 != AccountStateShadowRpcClient.ProtocolManifestSha256)
                throw new AccountStateShadowCompatibilityException();
            SetFailClosedState(verified);
            SaveJournal(ReadJournalSafely()! with { AuthorityState = NicknameAuthorityStates.Verified, AuthorityEpoch = verified.Epoch, Rows = JournalRows(finalNicknames, "verified") });
            await ActivateVerifiedMigrationAsync(client, verified, cancellationToken).ConfigureAwait(false);
            return;
        }

        SetFailClosed("source_generation_unstable");
    }

    private async Task ActivateVerifiedMigrationAsync(IAccountStateShadowClient client, NicknameAuthorityStatus verified, CancellationToken cancellationToken)
    {
        if (verified.State != NicknameAuthorityStates.Verified || string.IsNullOrWhiteSpace(verified.MigrationId))
            throw new AccountStateShadowCompatibilityException();

        var snapshot = _registry.ReadCommittedSnapshot();
        var nicknames = await ReadNicknamesAsync(client, snapshot.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
        var accounts = await client.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var mirrors = nicknames.ToDictionary(pair => pair.Key, pair => pair.Value.Nickname);
        if (!VerifyEffectiveAccountSet(snapshot.Profiles, accounts, nicknames) ||
            !_registry.VerifyPersistedNicknameMirrors(mirrors) || _registry.CurrentGeneration != snapshot.Generation)
        {
            SetFailClosed("verified_migration_proof_mismatch");
            return;
        }

        var active = await client.ActivateNicknameAuthorityAsync(verified.Epoch, verified.MigrationId, cancellationToken).ConfigureAwait(false);
        if (active.State != NicknameAuthorityStates.DaemonNicknameAuthority || active.Epoch <= verified.Epoch ||
            active.MigrationId != verified.MigrationId || active.ProtocolManifestSha256 != AccountStateShadowRpcClient.ProtocolManifestSha256)
            throw new AccountStateShadowCompatibilityException();

        var (info, readBack) = await ReadAuthorityStateAsync(client, cancellationToken).ConfigureAwait(false);
        if (readBack.State != NicknameAuthorityStates.DaemonNicknameAuthority || readBack.Epoch != active.Epoch || info.DatabaseSchemaVersion != 2)
            throw new AccountStateShadowCompatibilityException();
        SetFailClosedState(readBack);
        SaveJournal(ReadJournalSafely()! with
        {
            AuthorityState = NicknameAuthorityStates.DaemonNicknameAuthority,
            AuthorityEpoch = readBack.Epoch,
            AuthorityWasActivated = true,
            SourceGeneration = snapshot.Generation,
            Rows = JournalRows(nicknames, "active")
        });
        await RefreshActiveCacheAndSourceAsync(client, readBack, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountNicknameMutationResult> RenameAsync(Guid accountId, string nickname, CancellationToken cancellationToken = default)
    {
        if (!IsAuthorityPotentiallyActive)
            return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.LocalOnly, "legacy_local");

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var daemonLease = await _coordinator.AcquireNicknameMutationLeaseAsync(cancellationToken).ConfigureAwait(false);
            await using var client = await _clientFactory.ConnectNicknameAuthorityCompatibleAsync(cancellationToken).ConfigureAwait(false);
            var (info, status) = await ReadAuthorityStateAsync(client, cancellationToken).ConfigureAwait(false);
            SetFailClosedState(status);
            if (status.State is not (NicknameAuthorityStates.DaemonNicknameAuthority or NicknameAuthorityStates.Preparing))
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.FailedDiverged, "authority_not_writable");

            var profile = _registry.Find(accountId);
            if (profile is null)
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.NoOp, "account_missing");
            var snapshot = _registry.ReadCommittedSnapshot();
            var current = await ReadNicknamesAsync(client, snapshot.Profiles, allowMissing: true, cancellationToken).ConfigureAwait(false);
            var source = BuildSourceCollection(snapshot.Profiles, current, migrationIntents: null, legacyNicknamesAreRelevant: false);
            await client.ReconcileSourceAsync(source, status.Epoch, MigrationIdFor(status), cancellationToken).ConfigureAwait(false);
            var exact = await ReadNicknamesAsync(client, snapshot.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
            var desired = NormalizeNickname(nickname);
            var mutation = await TryApplyNicknameAsync(client, profile, desired, exact[accountId], status, MigrationIdFor(status), cancellationToken).ConfigureAwait(false);
            if (mutation.Conflict)
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.Conflict, "revision_conflict_different_name");

            var confirmed = await ReadNicknamesAsync(client, _registry.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
            var mirrorMap = confirmed.ToDictionary(pair => pair.Key, pair => pair.Value.Nickname);
            if (!ApplyAndVerifyMirrors(_registry.CurrentGeneration, mirrorMap))
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.FailedDiverged, "local_cache_persistence_failed");

            if (status.State == NicknameAuthorityStates.DaemonNicknameAuthority)
                SaveActiveJournal(status, _registry.ReadCommittedSnapshot(), confirmed);
            return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.DaemonAndLocal, "verified");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.FailedDiverged, ClassifyFailure(exception));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<AccountNicknameMutationResult> ResolveMigrationConflictAsync(Guid accountId, string selectedNickname, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var profileLease = await ProfileMigrationLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false);
            await using var daemonLease = await _coordinator.AcquireNicknameMutationLeaseAsync(cancellationToken).ConfigureAwait(false);
            await using var client = await _clientFactory.ConnectNicknameAuthorityCompatibleAsync(cancellationToken).ConfigureAwait(false);
            var (info, status) = await ReadAuthorityStateAsync(client, cancellationToken).ConfigureAwait(false);
            if (status.State != NicknameAuthorityStates.Preparing || string.IsNullOrWhiteSpace(status.MigrationId))
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.Conflict, "migration_state_changed");

            AccountNicknameAuthorityConflict? shown;
            lock (_stateSync)
                shown = _pendingConflicts.FirstOrDefault(conflict => conflict.AccountId == accountId);
            if (shown is null)
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.Conflict, "migration_conflict_context_missing");

            var snapshot = _registry.ReadCommittedSnapshot();
            var profile = snapshot.Profiles.FirstOrDefault(item => item.Id == accountId);
            if (profile is null || !string.Equals(NormalizeNickname(profile.Nickname), shown.LocalNickname, StringComparison.Ordinal))
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.Conflict, "profile_changed_during_prompt");
            var current = await client.GetNicknameAsync(accountId.ToString("D", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Nickname, shown.DaemonNickname, StringComparison.Ordinal))
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.Conflict, "daemon_changed_during_prompt");

            var resolved = await TryApplyNicknameAsync(client, profile, NormalizeNickname(selectedNickname), current, status, status.MigrationId, cancellationToken).ConfigureAwait(false);
            if (resolved.Conflict)
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.Conflict, "revision_conflict_different_name");

            var latest = await ReadNicknamesAsync(client, _registry.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
            if (!ApplyAndVerifyMirrors(_registry.CurrentGeneration, latest.ToDictionary(pair => pair.Key, pair => pair.Value.Nickname)))
                return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.FailedDiverged, "local_cache_persistence_failed");

            lock (_stateSync)
                _pendingConflicts.RemoveAll(conflict => conflict.AccountId == accountId);
            SaveRows(_registry.ReadCommittedSnapshot(), latest, "resolving");
            await ContinuePreparationAsync(client, status, cancellationToken).ConfigureAwait(false);
            return new AccountNicknameMutationResult(
                GetPendingConflicts().Count == 0 && IsAuthorityActive
                    ? AccountNicknameMutationOutcome.DaemonAndLocal
                    : AccountNicknameMutationOutcome.Conflict,
                GetPendingConflicts().Count == 0 ? "migration_resumed" : "additional_conflict_pending");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetFailClosed(ClassifyFailure(exception));
            return new AccountNicknameMutationResult(AccountNicknameMutationOutcome.FailedDiverged, ClassifyFailure(exception));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SynchronizeActiveSourceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAuthorityPotentiallyActive)
            return;
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var daemonLease = await _coordinator.AcquireNicknameMutationLeaseAsync(cancellationToken).ConfigureAwait(false);
            await using var client = await _clientFactory.ConnectNicknameAuthorityCompatibleAsync(cancellationToken).ConfigureAwait(false);
            var (_, status) = await ReadAuthorityStateAsync(client, cancellationToken).ConfigureAwait(false);
            if (status.State != NicknameAuthorityStates.DaemonNicknameAuthority)
                return;
            SetFailClosedState(status);
            var snapshot = _registry.ReadCommittedSnapshot();
            await ReconcileActiveSourceAsync(client, status, snapshot, migrationIntents: null, cancellationToken).ConfigureAwait(false);
            var exact = await ReadNicknamesAsync(client, _registry.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
            if (!ApplyAndVerifyMirrors(_registry.CurrentGeneration, exact.ToDictionary(pair => pair.Key, pair => pair.Value.Nickname)))
                SetFailClosed("local_cache_persistence_failed");
            else
                SaveActiveJournal(status, _registry.ReadCommittedSnapshot(), exact);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetFailClosed(ClassifyFailure(exception));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<AccountNicknameImportResult> ImportAsync(string document, CancellationToken cancellationToken = default)
    {
        if (!IsAuthorityPotentiallyActive)
            return new AccountNicknameImportResult(_transfer.Import(document), 0, 0);

        // Credential/profile import remains independently successful; nickname values stay in bounded memory only.
        var imported = _transfer.ImportWithoutNicknames(document);
        if (!IsAuthorityActive)
            return new AccountNicknameImportResult(imported.Count, 0, imported.Nicknames.Count);

        var applied = 0;
        var failed = 0;
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var daemonLease = await _coordinator.AcquireNicknameMutationLeaseAsync(cancellationToken).ConfigureAwait(false);
            await using var client = await _clientFactory.ConnectNicknameAuthorityCompatibleAsync(cancellationToken).ConfigureAwait(false);
            var (_, status) = await ReadAuthorityStateAsync(client, cancellationToken).ConfigureAwait(false);
            if (status.State != NicknameAuthorityStates.DaemonNicknameAuthority)
                return new AccountNicknameImportResult(imported.Count, 0, imported.Nicknames.Count);

            var intents = imported.Nicknames.ToDictionary(item => item.AccountId, item => item.Nickname);
            var snapshot = _registry.ReadCommittedSnapshot();
            await ReconcileActiveSourceAsync(client, status, snapshot, intents, cancellationToken).ConfigureAwait(false);
            var exact = await ReadNicknamesAsync(client, _registry.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
            foreach (var intent in imported.Nicknames)
            {
                var profile = _registry.Find(intent.AccountId);
                if (profile is null || !exact.TryGetValue(intent.AccountId, out var before))
                {
                    failed++;
                    continue;
                }
                var mutation = await TryApplyNicknameAsync(client, profile, intent.Nickname, before, status, migrationId: null, cancellationToken).ConfigureAwait(false);
                if (mutation.Conflict)
                    failed++;
                else
                    applied++;
            }

            var final = await ReadNicknamesAsync(client, _registry.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
            if (!ApplyAndVerifyMirrors(_registry.CurrentGeneration, final.ToDictionary(pair => pair.Key, pair => pair.Value.Nickname)))
                failed += Math.Max(1, imported.Nicknames.Count - applied);
            SaveActiveJournal(status, _registry.ReadCommittedSnapshot(), final);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failed += Math.Max(1, imported.Nicknames.Count - applied);
            SetFailClosed(ClassifyFailure(exception));
        }
        finally
        {
            _operationGate.Release();
        }
        return new AccountNicknameImportResult(imported.Count, applied, failed);
    }

    public async Task<string> ExportOneAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!IsAuthorityPotentiallyActive)
            return _transfer.ExportOne(accountId);
        var nicknames = await GetVerifiedActiveNicknamesAsync(cancellationToken).ConfigureAwait(false);
        if (!nicknames.TryGetValue(accountId, out var nickname))
            throw new InvalidOperationException("Current nickname state could not be verified for this account.");
        return _transfer.ExportOne(accountId, nickname);
    }

    public async Task<string> ExportAllAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAuthorityPotentiallyActive)
            return _transfer.ExportAll();
        var nicknames = await GetVerifiedActiveNicknamesAsync(cancellationToken).ConfigureAwait(false);
        return _transfer.ExportAll(nicknames);
    }

    public async Task<bool> RollbackAsync(CancellationToken cancellationToken = default)
    {
        await using var profileLease = await ProfileMigrationLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false);
        await using var daemonLease = await _coordinator.AcquireNicknameMutationLeaseAsync(cancellationToken).ConfigureAwait(false);
        await using var client = await _clientFactory.ConnectNicknameAuthorityCompatibleAsync(cancellationToken).ConfigureAwait(false);
        var (_, status) = await ReadAuthorityStateAsync(client, cancellationToken).ConfigureAwait(false);
        if (status.State == NicknameAuthorityStates.DaemonNicknameAuthority)
        {
            if (string.IsNullOrWhiteSpace(status.MigrationId))
                return false;
            var rollback = await client.BeginNicknameAuthorityRollbackAsync(status.Epoch, status.MigrationId, cancellationToken).ConfigureAwait(false);
            if (rollback.State != NicknameAuthorityStates.RollbackRequired || rollback.Epoch <= status.Epoch || rollback.MigrationId != status.MigrationId)
                return false;
            status = rollback;
            SetFailClosedState(status);
            SaveJournal(ReadJournalSafely()! with { AuthorityState = status.State, AuthorityEpoch = status.Epoch, AuthorityWasActivated = true });
        }
        return status.State == NicknameAuthorityStates.RollbackRequired && await CompleteRollbackAsync(client, status, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CompleteRollbackAsync(IAccountStateShadowClient client, NicknameAuthorityStatus status, CancellationToken cancellationToken)
    {
        if (status.State != NicknameAuthorityStates.RollbackRequired || string.IsNullOrWhiteSpace(status.MigrationId))
            return false;
        SetFailClosedState(status);
        var snapshot = _registry.ReadCommittedSnapshot();
        var nicknames = await ReadNicknamesAsync(client, snapshot.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
        var accounts = await client.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (!VerifyEffectiveAccountSet(snapshot.Profiles, accounts, nicknames))
        {
            SetFailClosed("rollback_account_set_mismatch");
            return false;
        }

        var mirrorMap = nicknames.ToDictionary(pair => pair.Key, pair => pair.Value.Nickname);
        if (!ApplyAndVerifyMirrors(snapshot.Generation, mirrorMap) || !_registry.VerifyPersistedNicknameMirrors(mirrorMap))
        {
            SetFailClosed("rollback_mirror_persistence_failed");
            return false;
        }

        var current = await client.GetNicknameAuthorityAsync(cancellationToken).ConfigureAwait(false);
        if (current.State != NicknameAuthorityStates.RollbackRequired || current.Epoch != status.Epoch || current.MigrationId != status.MigrationId)
        {
            SetFailClosed("rollback_epoch_changed");
            return false;
        }

        var completed = await client.CompleteNicknameAuthorityRollbackAsync(current.Epoch, status.MigrationId, cancellationToken).ConfigureAwait(false);
        if (completed.State != NicknameAuthorityStates.LegacyLocal || completed.Epoch <= current.Epoch || completed.MigrationId != status.MigrationId)
        {
            SetFailClosed("rollback_completion_unverified");
            return false;
        }

        SaveJournal(ReadJournalSafely()! with
        {
            AuthorityState = NicknameAuthorityStates.LegacyLocal,
            AuthorityEpoch = completed.Epoch,
            AuthorityWasActivated = true,
            RollbackCompleted = true,
            SourceGeneration = _registry.CurrentGeneration,
            Rows = JournalRows(nicknames, "rollback_complete")
        });
        SetLocalAuthority(NicknameAuthorityStates.LegacyLocal, verified: true, preserveActivationHistory: true);
        return true;
    }

    private async Task RefreshActiveCacheAndSourceAsync(IAccountStateShadowClient client, NicknameAuthorityStatus status, CancellationToken cancellationToken)
    {
        if (status.State != NicknameAuthorityStates.DaemonNicknameAuthority)
            throw new AccountStateShadowCompatibilityException();
        var snapshot = _registry.ReadCommittedSnapshot();
        await ReconcileActiveSourceAsync(client, status, snapshot, migrationIntents: null, cancellationToken).ConfigureAwait(false);
        var exact = await ReadNicknamesAsync(client, _registry.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
        var mirrorMap = exact.ToDictionary(pair => pair.Key, pair => pair.Value.Nickname);
        if (!ApplyAndVerifyMirrors(_registry.CurrentGeneration, mirrorMap) || !_registry.VerifyPersistedNicknameMirrors(mirrorMap))
        {
            SetFailClosed("active_cache_refresh_failed");
            return;
        }
        SaveActiveJournal(status, _registry.ReadCommittedSnapshot(), exact);
        SetFailClosedState(status);
    }

    private async Task ReconcileActiveSourceAsync(
        IAccountStateShadowClient client,
        NicknameAuthorityStatus status,
        AccountRegistrySnapshot snapshot,
        IReadOnlyDictionary<Guid, string>? migrationIntents,
        CancellationToken cancellationToken)
    {
        if (status.State != NicknameAuthorityStates.DaemonNicknameAuthority)
            throw new AccountStateShadowCompatibilityException();
        var existing = await ReadNicknamesAsync(client, snapshot.Profiles, allowMissing: true, cancellationToken).ConfigureAwait(false);
        var known = ReadJournalSafely()?.Rows.Select(row => row.AccountId).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var profile in snapshot.Profiles)
        {
            var id = profile.Id.ToString("D", CultureInfo.InvariantCulture);
            if (!existing.ContainsKey(profile.Id) && known.Contains(id))
                throw new AccountStateShadowCompatibilityException();
        }

        var source = BuildSourceCollection(snapshot.Profiles, existing, migrationIntents, legacyNicknamesAreRelevant: false);
        await client.ReconcileSourceAsync(source, status.Epoch, migrationId: null, cancellationToken).ConfigureAwait(false);
        var exact = await ReadNicknamesAsync(client, snapshot.Profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
        var accounts = await client.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (!VerifyEffectiveAccountSet(snapshot.Profiles, accounts, exact))
            throw new AccountStateShadowCompatibilityException();
        SaveActiveJournal(status, snapshot, exact);
    }

    private async Task<IReadOnlyDictionary<Guid, string?>> GetVerifiedActiveNicknamesAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var daemonLease = await _coordinator.AcquireNicknameMutationLeaseAsync(cancellationToken).ConfigureAwait(false);
            await using var client = await _clientFactory.ConnectNicknameAuthorityCompatibleAsync(cancellationToken).ConfigureAwait(false);
            var (_, status) = await ReadAuthorityStateAsync(client, cancellationToken).ConfigureAwait(false);
            if (status.State != NicknameAuthorityStates.DaemonNicknameAuthority)
                throw new InvalidOperationException("Current nickname authority is unavailable.");
            var profiles = _registry.ReadCommittedSnapshot().Profiles;
            var exact = await ReadNicknamesAsync(client, profiles, allowMissing: false, cancellationToken).ConfigureAwait(false);
            return exact.ToDictionary(pair => pair.Key, pair => pair.Value.Nickname);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static async Task<(bool Conflict, AccountNicknameState? Current)> TryApplyNicknameAsync(
        IAccountStateShadowClient client,
        ProfileMetadata profile,
        string? desired,
        AccountNicknameState before,
        NicknameAuthorityStatus status,
        string? migrationId,
        CancellationToken cancellationToken)
    {
        var accountId = profile.Id.ToString("D", CultureInfo.InvariantCulture);
        if (!string.Equals(before.AccountId, accountId, StringComparison.Ordinal))
            throw new AccountStateShadowCompatibilityException();
        if (string.Equals(before.Nickname, desired, StringComparison.Ordinal))
            return (false, before);

        var fallback = desired is null ? GetSafeBaseFallback(profile) : "Account";
        var mutation = new AccountNicknameMutation
        {
            AccountId = accountId,
            Nickname = desired,
            SafeFallbackDisplayName = fallback,
            ExpectedRevision = before.Revision
        };
        try
        {
            var result = await client.UpdateNicknameAsync(mutation, status.Epoch, migrationId, cancellationToken).ConfigureAwait(false);
            if (result.NicknameState.AccountId != accountId || !string.Equals(result.NicknameState.Nickname, desired, StringComparison.Ordinal))
                throw new AccountStateShadowCompatibilityException();
        }
        catch (AccountStateShadowRpcException exception) when (exception.Category == "revision_conflict")
        {
            var observed = await client.GetNicknameAsync(accountId, cancellationToken).ConfigureAwait(false);
            if (string.Equals(observed.Nickname, desired, StringComparison.Ordinal))
                return (false, observed);
            return (true, observed);
        }

        var readBack = await client.GetNicknameAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (readBack.AccountId != accountId || !string.Equals(readBack.Nickname, desired, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(readBack.Revision))
            throw new AccountStateShadowCompatibilityException();
        return (false, readBack);
    }

    private static AccountSourceCollection BuildSourceCollection(
        IReadOnlyList<ProfileMetadata> profiles,
        Dictionary<Guid, AccountNicknameState> nicknames,
        IReadOnlyDictionary<Guid, string>? migrationIntents,
        bool legacyNicknamesAreRelevant)
    {
        var accounts = profiles.OrderBy(profile => profile.SortOrder).ThenBy(profile => profile.CreatedAt).Select(profile =>
        {
            var hasDaemonNickname = nicknames.TryGetValue(profile.Id, out var state) && state.Nickname is not null;
            var hasPendingNickname = migrationIntents?.ContainsKey(profile.Id) == true;
            var hasLegacyNickname = legacyNicknamesAreRelevant && NormalizeNickname(profile.Nickname) is not null;
            return new AccountSourceDescriptor
            {
                AccountId = profile.Id.ToString("D", CultureInfo.InvariantCulture),
                SafeFallbackDisplayName = hasDaemonNickname || hasPendingNickname || hasLegacyNickname
                    ? "Account"
                    : GetSafeBaseFallback(profile),
                Health = profile.HealthStatus switch
                {
                    CodexSwitcher.Core.Common.Enums.HealthStatus.Valid => "valid",
                    CodexSwitcher.Core.Common.Enums.HealthStatus.NeedsReLogin => "needs_relogin",
                    _ => "unknown"
                },
                IsActive = profile.IsActive,
                SortOrder = profile.SortOrder,
                CreatedAt = profile.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                PlanType = profile.PlanType
            };
        }).ToArray();
        return new AccountSourceCollection { Accounts = accounts };
    }

    private static async Task<Dictionary<Guid, AccountNicknameState>> ReadNicknamesAsync(
        IAccountStateShadowClient client,
        IReadOnlyList<ProfileMetadata> profiles,
        bool allowMissing,
        CancellationToken cancellationToken)
    {
        var response = await client.ListNicknamesAsync(cancellationToken).ConfigureAwait(false);
        var localIds = profiles.Select(profile => profile.Id).ToHashSet();
        var result = new Dictionary<Guid, AccountNicknameState>();
        foreach (var state in response.Accounts)
        {
            if (!Guid.TryParseExact(state.AccountId, "D", out var id) || !string.Equals(state.AccountId, id.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new AccountStateShadowCompatibilityException();
            if (localIds.Contains(id) && !result.TryAdd(id, state))
                throw new AccountStateShadowCompatibilityException();
        }
        if (!allowMissing && (result.Count != profiles.Count || profiles.Any(profile => !result.ContainsKey(profile.Id))))
            throw new AccountStateShadowCompatibilityException();
        return result;
    }

    private static bool VerifyEffectiveAccountSet(
        IReadOnlyList<ProfileMetadata> profiles,
        IReadOnlyList<AccountDescriptor> actual,
        Dictionary<Guid, AccountNicknameState> nicknames)
    {
        if (actual.Count != profiles.Count || nicknames.Count != profiles.Count)
            return false;
        var byId = new Dictionary<Guid, AccountDescriptor>();
        foreach (var account in actual)
        {
            if (!Guid.TryParseExact(account.AccountId, "D", out var id) || !byId.TryAdd(id, account))
                return false;
        }

        foreach (var profile in profiles)
        {
            if (!byId.TryGetValue(profile.Id, out var account) || !nicknames.TryGetValue(profile.Id, out var nickname))
                return false;
            var fallback = nickname.Nickname is null ? GetSafeBaseFallback(profile) : "Account";
            var expectedName = nickname.Nickname ?? fallback;
            var expectedHealth = profile.HealthStatus switch
            {
                CodexSwitcher.Core.Common.Enums.HealthStatus.Valid => "valid",
                CodexSwitcher.Core.Common.Enums.HealthStatus.NeedsReLogin => "needs_relogin",
                _ => "unknown"
            };
            if (account.SchemaVersion != 1 || account.AccountId != profile.Id.ToString("D", CultureInfo.InvariantCulture) ||
                account.DisplayName != expectedName || account.Health != expectedHealth || account.IsActive != profile.IsActive ||
                account.SortOrder != profile.SortOrder || !SameInstant(account.CreatedAt, profile.CreatedAt) || account.PlanType != profile.PlanType)
                return false;
        }
        return true;
    }

    private static async Task<(AuthorityDaemonInfo Info, NicknameAuthorityStatus Status)> ReadAuthorityStateAsync(
        IAccountStateShadowClient client,
        CancellationToken cancellationToken)
    {
        if (!client.SupportsNicknameAuthority)
            throw new AccountStateShadowCompatibilityException();
        var info = await client.GetAuthorityDaemonInfoAsync(cancellationToken).ConfigureAwait(false);
        var status = await client.GetNicknameAuthorityAsync(cancellationToken).ConfigureAwait(false);
        if (!NicknameAuthorityStates.IsKnown(info.AuthorityState) || info.AuthorityState != status.State ||
            status.ProtocolManifestSha256 != AccountStateShadowRpcClient.ProtocolManifestSha256 || info.DatabaseSchemaVersion is not (1 or 2))
            throw new AccountStateShadowCompatibilityException();
        var required = new[]
        {
            "shadow.accounts.reconcile", "accounts.updateMetadata", "accounts.getNickname", "accounts.listNicknames",
            "accounts.updateNickname", "shadow.accounts.reconcileSource", "accounts.nicknameAuthority.get", "accounts.nicknameAuthority.lifecycle"
        };
        if (required.Any(id => !info.CapabilityEnabled.ContainsKey(id)))
            throw new AccountStateShadowCompatibilityException();
        return (info, status);
    }

    private async Task EnsurePreparingJournalAsync(NicknameAuthorityStatus status, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _registry.ReadCommittedSnapshot();
        var existing = ReadJournalSafely();
        if (existing?.MigrationId is not null && existing.MigrationId != status.MigrationId)
            throw new AccountStateShadowCompatibilityException();
        SaveJournal(new NicknameAuthorityJournal
        {
            AuthorityState = status.State,
            AuthorityEpoch = status.Epoch,
            MigrationId = status.MigrationId,
            SourceGeneration = snapshot.Generation,
            Rows = existing?.Rows ?? snapshot.Profiles.Select(profile => new NicknameAuthorityMigrationRow
            {
                AccountId = profile.Id.ToString("D", CultureInfo.InvariantCulture),
                Phase = "resume"
            }).ToArray(),
            AuthorityWasActivated = existing?.AuthorityWasActivated ?? false
        });
    }

    private Task EnsureActiveJournalAsync(NicknameAuthorityStatus status, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var existing = ReadJournalSafely();
        if (existing?.MigrationId is not null && existing.MigrationId != status.MigrationId)
            throw new AccountStateShadowCompatibilityException();
        var snapshot = _registry.ReadCommittedSnapshot();
        SaveJournal(new NicknameAuthorityJournal
        {
            AuthorityState = status.State,
            AuthorityEpoch = status.Epoch,
            MigrationId = status.MigrationId,
            SourceGeneration = snapshot.Generation,
            AuthorityWasActivated = true,
            Rows = existing?.Rows ?? snapshot.Profiles.Select(profile => new NicknameAuthorityMigrationRow
            {
                AccountId = profile.Id.ToString("D", CultureInfo.InvariantCulture),
                Phase = "recovered_active"
            }).ToArray()
        });
        return Task.CompletedTask;
    }

    private void SaveActiveJournal(NicknameAuthorityStatus status, AccountRegistrySnapshot snapshot, IReadOnlyDictionary<Guid, AccountNicknameState> nicknames)
    {
        SaveJournal(new NicknameAuthorityJournal
        {
            AuthorityState = NicknameAuthorityStates.DaemonNicknameAuthority,
            AuthorityEpoch = status.Epoch,
            MigrationId = status.MigrationId,
            SourceGeneration = snapshot.Generation,
            AuthorityWasActivated = true,
            Rows = JournalRows(nicknames, "active")
        });
    }

    private void SaveRows(AccountRegistrySnapshot snapshot, IReadOnlyDictionary<Guid, AccountNicknameState> nicknames, string phase)
    {
        var existing = ReadJournalSafely();
        SaveJournal(new NicknameAuthorityJournal
        {
            AuthorityState = NicknameAuthorityStates.Preparing,
            AuthorityEpoch = _authorityState?.Epoch ?? existing?.AuthorityEpoch ?? 0,
            MigrationId = _authorityState?.MigrationId ?? existing?.MigrationId,
            SourceGeneration = snapshot.Generation,
            AuthorityWasActivated = existing?.AuthorityWasActivated ?? false,
            Rows = nicknames.Select(pair => new NicknameAuthorityMigrationRow
            {
                AccountId = pair.Key.ToString("D", CultureInfo.InvariantCulture),
                DaemonRevision = pair.Value.Revision,
                Phase = phase
            }).ToArray()
        });
    }

    private static NicknameAuthorityMigrationRow[] JournalRows(IReadOnlyDictionary<Guid, AccountNicknameState> nicknames, string phase) =>
        nicknames.Select(pair => new NicknameAuthorityMigrationRow
        {
            AccountId = pair.Key.ToString("D", CultureInfo.InvariantCulture),
            DaemonRevision = pair.Value.Revision,
            Phase = phase
        }).ToArray();

    private bool ApplyAndVerifyMirrors(long generation, IReadOnlyDictionary<Guid, string?> mirrors)
    {
        Interlocked.Exchange(ref _mirrorUpdateInProgress, 1);
        try
        {
            if (!_registry.UpdateNicknameMirrorsIfGeneration(mirrors, generation))
                return false;
            return _registry.VerifyPersistedNicknameMirrors(mirrors);
        }
        finally
        {
            Interlocked.Exchange(ref _mirrorUpdateInProgress, 0);
        }
    }

    private void SetAuthorityState(NicknameAuthorityStatus status)
    {
        lock (_stateSync)
            _authorityState = status;
    }

    private void SetFailClosedState(NicknameAuthorityStatus status)
    {
        lock (_stateSync)
        {
            _authorityState = status;
            _authorityMayBeActive = NicknameAuthorityStates.IsAuthorityPossible(status.State) || _journalState?.AuthorityWasActivated == true;
            _localAuthorityVerified = false;
        }
        _registry.SetNicknameWritesFenced(true);
    }

    private void SetFailClosed(string category)
    {
        lock (_stateSync)
        {
            _authorityMayBeActive = true;
            _localAuthorityVerified = false;
        }
        _registry.SetNicknameWritesFenced(true);
        System.Diagnostics.Debug.WriteLine($"[NicknameAuthority] state=read_only category={SanitizeCategory(category)}");
    }

    private void SetLocalAuthority(string state, bool verified, bool preserveActivationHistory = false)
    {
        lock (_stateSync)
        {
            _authorityState = new NicknameAuthorityStatus
            {
                State = state,
                Epoch = ReadJournalSafely()?.AuthorityEpoch ?? 0,
                ProtocolManifestSha256 = AccountStateShadowRpcClient.ProtocolManifestSha256,
                MigrationId = ReadJournalSafely()?.MigrationId
            };
            _authorityMayBeActive = false;
            _localAuthorityVerified = verified;
        }
        _registry.SetNicknameWritesFenced(!verified);
    }

    private NicknameAuthorityJournal? ReadJournalSafely()
    {
        try
        {
            var value = _journal.Read();
            lock (_stateSync)
                _journalState = value;
            return value;
        }
        catch
        {
            lock (_stateSync)
            {
                _journalInvalid = true;
                _authorityMayBeActive = true;
                _localAuthorityVerified = false;
            }
            _registry.SetNicknameWritesFenced(true);
            return null;
        }
    }

    private void SaveJournal(NicknameAuthorityJournal value)
    {
        _journal.Save(value);
        lock (_stateSync)
        {
            _journalState = value;
            _authorityMayBeActive = NicknameAuthorityStates.IsAuthorityPossible(value.AuthorityState) || value.AuthorityWasActivated;
        }
    }

    private static bool NicknamesEqual(Dictionary<Guid, AccountNicknameState> left, Dictionary<Guid, AccountNicknameState> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var other) &&
            string.Equals(pair.Value.Nickname, other.Nickname, StringComparison.Ordinal) &&
            string.Equals(pair.Value.Revision, other.Revision, StringComparison.Ordinal));

    private static bool SameInstant(string timestamp, DateTimeOffset expected) =>
        DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var actual) &&
        actual == expected.ToUniversalTime();

    private static string? NormalizeNickname(string? nickname) => string.IsNullOrWhiteSpace(nickname) ? null : nickname;

    private static string GetSafeBaseFallback(ProfileMetadata profile) =>
        !string.IsNullOrWhiteSpace(profile.DetectedDisplayName) && !profile.DetectedDisplayName.Contains('@')
            ? profile.DetectedDisplayName.Trim()
            : "Account";

    private static string? MigrationIdFor(NicknameAuthorityStatus status) =>
        status.State == NicknameAuthorityStates.Preparing ? status.MigrationId : null;

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        AccountStateShadowRpcException { Category: { } category } => category,
        AccountStateShadowRpcTimeoutException => "timeout",
        AccountStateShadowCompatibilityException or JsonException => "protocol_incompatible",
        AccountStateShadowUnavailableException or IOException => "daemon_unavailable",
        UnauthorizedAccessException => "access_denied",
        TimeoutException => "timeout",
        InvalidDataException => "journal_invalid",
        _ => "operation_failed"
    };

    private static string SanitizeCategory(string category) => category.Length <= 64 && category.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
        ? category
        : "operation_failed";

    private void OnRegistrySnapshotChanged()
    {
        if (Volatile.Read(ref _mirrorUpdateInProgress) != 0 || !IsAuthorityActive || Interlocked.Exchange(ref _activeSourceRefreshQueued, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try { await SynchronizeActiveSourceAsync().ConfigureAwait(false); }
            catch { }
            finally { Interlocked.Exchange(ref _activeSourceRefreshQueued, 0); }
        });
    }

    public void Dispose()
    {
        _registry.SnapshotChanged -= OnRegistrySnapshotChanged;
        _initializeGate.Dispose();
        _operationGate.Dispose();
    }
}
