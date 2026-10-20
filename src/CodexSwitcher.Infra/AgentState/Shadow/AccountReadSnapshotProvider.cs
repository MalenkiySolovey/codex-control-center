using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Services;

namespace CodexSwitcher.Infra.AgentState.Shadow;

/// <summary>Serves only canonical descriptors; full ProfileMetadata remains owned by AccountRegistry.</summary>
public sealed class AccountReadSnapshotProvider : IAccountReadSnapshotProvider
{
    private readonly AccountRegistry _registry;
    private readonly IAccountStateShadowReadAuthority _coordinator;
    private readonly AccountReadAuthorityCanarySettings _canarySettings;
    private readonly AccountReadAuthorityCanaryDiagnostics? _diagnostics;

    public AccountReadSnapshotProvider(
        AccountRegistry registry,
        IAccountStateShadowReadAuthority coordinator,
        AccountReadAuthorityCanarySettings canarySettings,
        AccountReadAuthorityCanaryDiagnostics? diagnostics = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _canarySettings = canarySettings ?? throw new ArgumentNullException(nameof(canarySettings));
        _diagnostics = diagnostics;
    }

    public AccountReadSnapshot Read()
    {
        var committed = _registry.ReadCommittedSnapshot();
        var localAccounts = AccountStateShadowCoordinator.ProjectProfiles(committed.Profiles);
        if (!_canarySettings.Requested)
        {
            return Record(CreateSnapshot(
                AccountReadSource.Local,
                committed.Generation,
                localAccounts,
                AccountReadCanaryState.Off,
                false,
                null));
        }

        if (!_canarySettings.Enabled)
        {
            return Record(CreateSnapshot(
                AccountReadSource.Local,
                committed.Generation,
                localAccounts,
                AccountReadCanaryState.ShadowDisabled,
                false,
                null));
        }

        var readState = _coordinator.ReadAuthorityState;
        var status = readState.Status;
        var canServeDaemon =
            string.Equals(status.Category, "synchronized", StringComparison.Ordinal) &&
            readState.MatchedGeneration == committed.Generation &&
            status.LastMatchedGeneration == committed.Generation &&
            readState.MatchedAccounts is not null &&
            _coordinator.IsDaemonConnectionHealthy &&
            _registry.CurrentGeneration == committed.Generation &&
            AccountStateShadowCoordinator.CountMismatches(localAccounts, readState.MatchedAccounts) == 0;

        if (canServeDaemon)
        {
            return Record(CreateSnapshot(
                AccountReadSource.DaemonCanary,
                committed.Generation,
                readState.MatchedAccounts!,
                AccountReadCanaryState.DaemonActive,
                true,
                status));
        }

        return Record(CreateSnapshot(
            AccountReadSource.Local,
            committed.Generation,
            localAccounts,
            GetFallbackState(committed.Generation, status, _coordinator.IsDaemonConnectionHealthy),
            true,
            status));
    }

    private static AccountReadSnapshot CreateSnapshot(
        AccountReadSource source,
        long generation,
        IReadOnlyList<CodexSwitcher.Core.AgentState.V1.AccountDescriptor> accounts,
        AccountReadCanaryState canaryState,
        bool canaryEnabled,
        AccountStateShadowStatus? status) =>
        new(
            source,
            generation,
            Array.AsReadOnly(accounts.ToArray()),
            accounts.Count,
            canaryState,
            canaryEnabled,
            status?.LastRequestedGeneration ?? 0,
            status?.LastMatchedGeneration,
            status?.MismatchCount ?? 0,
            status?.LastTransitionAt,
            status?.DurationMilliseconds ?? 0);

    private AccountReadSnapshot Record(AccountReadSnapshot snapshot)
    {
        _diagnostics?.Record(snapshot);
        return snapshot;
    }

    private static AccountReadCanaryState GetFallbackState(
        long currentGeneration,
        AccountStateShadowStatus status,
        bool daemonConnectionHealthy) => status.Category switch
        {
            "incompatible" => AccountReadCanaryState.LocalFallbackProtocolIncompatible,
            "mismatch" => AccountReadCanaryState.LocalFallbackMismatch,
            "unavailable" => AccountReadCanaryState.LocalFallbackDaemonUnavailable,
            "sync_failed" or "worker_failed" => AccountReadCanaryState.LocalFallbackFailed,
            "stopped" => AccountReadCanaryState.LocalFallbackDaemonUnavailable,
            "nickname_mutation_pending" => AccountReadCanaryState.LocalFallbackDirty,
            "synchronized" when status.LastMatchedGeneration != currentGeneration => AccountReadCanaryState.LocalFallbackDirty,
            "stale_generation" => AccountReadCanaryState.LocalFallbackDirty,
            "synchronized" when !daemonConnectionHealthy => AccountReadCanaryState.LocalFallbackDaemonUnavailable,
            "synchronizing" when status.LastMatchedGeneration is null => AccountReadCanaryState.PendingInitialSync,
            "synchronizing" when status.LastRequestedGeneration > 0 => AccountReadCanaryState.LocalFallbackDirty,
            _ when currentGeneration > 0 && status.LastRequestedGeneration > 0 => AccountReadCanaryState.LocalFallbackDirty,
            _ => AccountReadCanaryState.PendingInitialSync
        };
}
