using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.AgentState.V1;

namespace CodexSwitcher.Infra.AgentState.Shadow;

internal sealed class AccountStateShadowCoordinator : IDisposable, IAccountStateShadowReadAuthority
{
    private static readonly TimeSpan RevalidationInterval = TimeSpan.FromSeconds(5);

    private readonly AccountRegistry _registry;
    private readonly AccountStateShadowSettings _settings;
    private readonly bool _readAuthorityCanaryEnabled;
    private readonly IAccountStateShadowClientFactory _clientFactory;
    private readonly AccountNicknameMutationDiagnostics? _nicknameDiagnostics;
    private readonly SemaphoreSlim _daemonOperationGate = new(1, 1);
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly object _lifecycleSync = new();
    private CancellationTokenSource? _stopSource;
    private Task? _worker;
    private AccountStateShadowReadState _readState;

    public AccountStateShadowCoordinator(
        AccountRegistry registry,
        AccountStateShadowSettings settings,
        IAccountStateShadowClientFactory clientFactory,
        AccountReadAuthorityCanarySettings? canarySettings = null,
        AccountNicknameMutationDiagnostics? nicknameDiagnostics = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _readAuthorityCanaryEnabled = canarySettings?.Enabled == true;
        _nicknameDiagnostics = nicknameDiagnostics;
        _readState = new AccountStateShadowReadState(
            new AccountStateShadowStatus(
                settings.Enabled ? "starting" : settings.StartupCategory,
                CurrentGeneration: registry.CurrentGeneration),
            null,
            null);
    }

    public AccountStateShadowStatus LastStatus => Volatile.Read(ref _readState).Status;

    public AccountStateShadowReadState ReadAuthorityState => Volatile.Read(ref _readState);

    public bool IsDaemonConnectionHealthy => _clientFactory.IsLastConnectionHealthy;

    internal async ValueTask<IAsyncDisposable> AcquireNicknameMutationLeaseAsync(CancellationToken cancellationToken)
    {
        await _daemonOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SemaphoreLease(_daemonOperationGate);
    }

    internal void MarkNicknameMutationPending(long generation, int accountCount) =>
        SetStatus("nickname_mutation_pending", accountCount, requestedGeneration: generation);

    internal void RequestReconcile() => SignalSnapshotChanged();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            SetStatus(_settings.StartupCategory);
            return Task.CompletedTask;
        }

        lock (_lifecycleSync)
        {
            if (_worker is not null)
                return Task.CompletedTask;

            cancellationToken.ThrowIfCancellationRequested();
            _stopSource = new CancellationTokenSource();
            var stopToken = _stopSource.Token;
            _registry.SnapshotChanged += OnSnapshotChanged;
            _worker = Task.Run(() => ProcessSignalsAsync(stopToken), CancellationToken.None);
            if (_registry.IsLoaded)
                SignalSnapshotChanged();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? worker;
        lock (_lifecycleSync)
        {
            if (_worker is null)
            {
                worker = null;
            }
            else
            {
                _registry.SnapshotChanged -= OnSnapshotChanged;
                _stopSource?.Cancel();
                _signals.Writer.TryComplete();
                worker = _worker;
            }
        }

        if (worker is not null)
        {
            try
            {
                await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
            }
        }

        await _clientFactory.StopOwnedProcessAsync(cancellationToken).ConfigureAwait(false);
        SetStatus(_settings.Enabled ? "stopped" : _settings.StartupCategory);
        lock (_lifecycleSync)
        {
            _stopSource?.Dispose();
            _stopSource = null;
            _worker = null;
        }
    }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            _registry.SnapshotChanged -= OnSnapshotChanged;
            _stopSource?.Cancel();
            _signals.Writer.TryComplete();
            _stopSource?.Dispose();
            _stopSource = null;
        }
    }

    private void OnSnapshotChanged() => SignalSnapshotChanged();

    private void SignalSnapshotChanged() => _signals.Writer.TryWrite(0);

    private async Task ProcessSignalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            using var timer = new PeriodicTimer(RevalidationInterval);
            var signalWait = _signals.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var timerWait = timer.WaitForNextTickAsync(cancellationToken).AsTask();

            while (true)
            {
                await Task.WhenAny(signalWait, timerWait).ConfigureAwait(false);
                var hasSignal = false;
                var hasTick = false;

                if (signalWait.IsCompleted)
                {
                    hasSignal = await signalWait.ConfigureAwait(false);
                    if (!hasSignal)
                        return;

                    while (_signals.Reader.TryRead(out _))
                    {
                    }

                    signalWait = _signals.Reader.WaitToReadAsync(cancellationToken).AsTask();
                }

                if (timerWait.IsCompleted)
                {
                    hasTick = await timerWait.ConfigureAwait(false);
                    if (!hasTick)
                        return;

                    timerWait = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                }

                if (hasSignal)
                    await ReconcileLatestSnapshotAsync(cancellationToken).ConfigureAwait(false);
                else if (hasTick && _readAuthorityCanaryEnabled && _registry.IsLoaded)
                    await VerifyLatestSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            SetStatus("worker_failed");
        }
    }

    private async Task ReconcileLatestSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var gate = await AcquireNicknameMutationLeaseAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = _registry.ReadCommittedSnapshot();
        var expected = ProjectProfiles(snapshot.Profiles);
        SetStatus("synchronizing", expected.Count, requestedGeneration: snapshot.Generation);

        try
        {
            await using var client = await _clientFactory.ConnectCompatibleAsync(cancellationToken).ConfigureAwait(false);
            // Keep the existing canonical projection semantics: reconciliation may rewrite the
            // daemon's internal nickname to the projected display name. This known flattening
            // behavior is intentionally not altered by the nickname mutation canary.
            var counts = await client.ReconcileAsync(new AccountCollection(1, expected), cancellationToken).ConfigureAwait(false);
            var actual = await client.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
            var mismatches = CountMismatches(expected, actual);
            stopwatch.Stop();

            if (mismatches != 0)
            {
                SetStatus("mismatch", expected.Count, mismatches, counts.Inserted, counts.Updated,
                    counts.Deleted, counts.Unchanged, stopwatch.ElapsedMilliseconds,
                    requestedGeneration: snapshot.Generation);
                return;
            }

            if (_registry.CurrentGeneration != snapshot.Generation)
            {
                SetStatus("stale_generation", expected.Count, durationMilliseconds: stopwatch.ElapsedMilliseconds,
                    requestedGeneration: snapshot.Generation);
                return;
            }

            SetStatus("synchronized", expected.Count, inserted: counts.Inserted, updated: counts.Updated,
                deleted: counts.Deleted, unchanged: counts.Unchanged,
                durationMilliseconds: stopwatch.ElapsedMilliseconds,
                requestedGeneration: snapshot.Generation,
                matchedGeneration: snapshot.Generation,
                matchedAccounts: Array.AsReadOnly(actual.ToArray()));
            _nicknameDiagnostics?.MarkConverged(snapshot.Generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AccountStateShadowCompatibilityException)
        {
            stopwatch.Stop();
            SetStatus("incompatible", durationMilliseconds: stopwatch.ElapsedMilliseconds,
                requestedGeneration: snapshot.Generation);
        }
        catch (AccountStateShadowRpcException exception) when (exception.Code == -32601)
        {
            stopwatch.Stop();
            SetStatus("incompatible", durationMilliseconds: stopwatch.ElapsedMilliseconds,
                requestedGeneration: snapshot.Generation);
        }
        catch (JsonException)
        {
            stopwatch.Stop();
            SetStatus("incompatible", durationMilliseconds: stopwatch.ElapsedMilliseconds,
                requestedGeneration: snapshot.Generation);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or AccountStateShadowUnavailableException)
        {
            stopwatch.Stop();
            SetStatus("unavailable", durationMilliseconds: stopwatch.ElapsedMilliseconds,
                requestedGeneration: snapshot.Generation);
        }
        catch (Exception)
        {
            stopwatch.Stop();
            SetStatus("sync_failed", durationMilliseconds: stopwatch.ElapsedMilliseconds,
                requestedGeneration: snapshot.Generation);
        }
    }

    private async Task VerifyLatestSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var gate = await AcquireNicknameMutationLeaseAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = _registry.ReadCommittedSnapshot();
        var expected = ProjectProfiles(snapshot.Profiles);
        try
        {
            await using var client = await _clientFactory.ConnectCompatibleAsync(cancellationToken).ConfigureAwait(false);
            var actual = await client.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
            var mismatches = CountMismatches(expected, actual);
            stopwatch.Stop();

            if (_registry.CurrentGeneration != snapshot.Generation)
            {
                SetStatus("stale_generation", expected.Count, mismatches,
                    durationMilliseconds: stopwatch.ElapsedMilliseconds,
                    requestedGeneration: snapshot.Generation);
                return;
            }

            if (mismatches != 0)
            {
                SetStatus("mismatch", expected.Count, mismatches,
                    durationMilliseconds: stopwatch.ElapsedMilliseconds,
                    requestedGeneration: snapshot.Generation);
                SignalSnapshotChanged();
                return;
            }

            SetStatus("synchronized", expected.Count,
                durationMilliseconds: stopwatch.ElapsedMilliseconds,
                requestedGeneration: snapshot.Generation,
                matchedGeneration: snapshot.Generation,
                matchedAccounts: Array.AsReadOnly(actual.ToArray()));
            _nicknameDiagnostics?.MarkConverged(snapshot.Generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AccountStateShadowCompatibilityException)
        {
            stopwatch.Stop();
            SetStatus("incompatible", durationMilliseconds: stopwatch.ElapsedMilliseconds,
                requestedGeneration: snapshot.Generation);
        }
        catch (AccountStateShadowRpcException exception) when (exception.Code == -32601)
        {
            stopwatch.Stop();
            SetStatus("incompatible", durationMilliseconds: stopwatch.ElapsedMilliseconds,
                requestedGeneration: snapshot.Generation);
        }
        catch (JsonException)
        {
            stopwatch.Stop();
            SetStatus("incompatible", durationMilliseconds: stopwatch.ElapsedMilliseconds,
                requestedGeneration: snapshot.Generation);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or AccountStateShadowUnavailableException)
        {
            stopwatch.Stop();
            SetStatus("unavailable", durationMilliseconds: stopwatch.ElapsedMilliseconds,
                requestedGeneration: snapshot.Generation);
        }
        catch (Exception)
        {
            stopwatch.Stop();
            SetStatus("sync_failed", durationMilliseconds: stopwatch.ElapsedMilliseconds,
                requestedGeneration: snapshot.Generation);
        }
    }

    internal static ReadOnlyCollection<AccountDescriptor> ProjectProfiles(IReadOnlyList<ProfileMetadata> profiles)
    {
        var ordered = profiles
            .OrderBy(profile => profile.SortOrder)
            .ThenByDescending(profile => profile.CreatedAt)
            .ToArray();
        return Array.AsReadOnly(ordered
            .Select((profile, index) => AccountDescriptorProjector.FromProfile(profile, index + 1))
            .ToArray());
    }

    internal static int CountMismatches(IReadOnlyList<AccountDescriptor> expected, IReadOnlyList<AccountDescriptor> actual)
    {
        var expectedIds = expected.Select(account => account.AccountId).ToHashSet(StringComparer.Ordinal);
        var actualById = new Dictionary<string, AccountDescriptor>(StringComparer.Ordinal);
        var mismatchCount = 0;
        foreach (var account in actual)
        {
            if (!actualById.TryAdd(account.AccountId, account))
                mismatchCount++;
        }

        foreach (var account in expected)
        {
            if (!actualById.TryGetValue(account.AccountId, out var returned))
            {
                mismatchCount++;
                continue;
            }

            if (!ProjectionEquals(account, returned))
                mismatchCount++;
        }

        mismatchCount += actual.Count(account => !expectedIds.Contains(account.AccountId));
        return mismatchCount;
    }

    private static bool ProjectionEquals(AccountDescriptor expected, AccountDescriptor actual) =>
        expected.SchemaVersion == actual.SchemaVersion &&
        string.Equals(expected.AccountId, actual.AccountId, StringComparison.Ordinal) &&
        string.Equals(expected.DisplayName, actual.DisplayName, StringComparison.Ordinal) &&
        string.Equals(expected.Health, actual.Health, StringComparison.Ordinal) &&
        expected.IsActive == actual.IsActive &&
        expected.SortOrder == actual.SortOrder &&
        SameUtcTimestamp(expected.CreatedAt, actual.CreatedAt) &&
        string.Equals(expected.PlanType, actual.PlanType, StringComparison.Ordinal);

    private static bool SameUtcTimestamp(string expected, string actual) =>
        DateTimeOffset.TryParse(expected, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expectedAt) &&
        DateTimeOffset.TryParse(actual, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var actualAt) &&
        expectedAt == actualAt;

    private void SetStatus(
        string category,
        int accountCount = 0,
        int mismatchCount = 0,
        int inserted = 0,
        int updated = 0,
        int deleted = 0,
        int unchanged = 0,
        long durationMilliseconds = 0,
        long? requestedGeneration = null,
        long? matchedGeneration = null,
        IReadOnlyList<AccountDescriptor>? matchedAccounts = null)
    {
        var previous = Volatile.Read(ref _readState);
        var lastMatchedGeneration = matchedGeneration ?? previous.MatchedGeneration;
        var lastMatchedAccounts = matchedAccounts ?? previous.MatchedAccounts;
        var lastTransitionAt = string.Equals(previous.Status.Category, category, StringComparison.Ordinal)
            ? previous.Status.LastTransitionAt ?? DateTimeOffset.UtcNow
            : DateTimeOffset.UtcNow;
        var status = new AccountStateShadowStatus(
            category,
            accountCount,
            mismatchCount,
            inserted,
            updated,
            deleted,
            unchanged,
            durationMilliseconds,
            _registry.CurrentGeneration,
            requestedGeneration ?? previous.Status.LastRequestedGeneration,
            lastMatchedGeneration,
            lastTransitionAt);
        Interlocked.Exchange(ref _readState, new AccountStateShadowReadState(status, lastMatchedGeneration, lastMatchedAccounts));
    }

    private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
