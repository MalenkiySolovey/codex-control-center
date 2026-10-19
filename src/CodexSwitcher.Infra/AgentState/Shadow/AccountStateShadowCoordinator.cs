using System.Globalization;
using System.Collections.ObjectModel;
using System.Threading.Channels;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.AgentState.V1;

namespace CodexSwitcher.Infra.AgentState.Shadow;

internal sealed class AccountStateShadowCoordinator : IDisposable
{
    private readonly AccountRegistry _registry;
    private readonly AccountStateShadowSettings _settings;
    private readonly IAccountStateShadowClientFactory _clientFactory;
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
    private AccountStateShadowStatus _lastStatus;

    public AccountStateShadowCoordinator(
        AccountRegistry registry,
        AccountStateShadowSettings settings,
        IAccountStateShadowClientFactory clientFactory)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _lastStatus = new AccountStateShadowStatus(settings.Enabled ? "starting" : settings.StartupCategory);
    }

    public AccountStateShadowStatus LastStatus => Volatile.Read(ref _lastStatus);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            SetStatus(new AccountStateShadowStatus(_settings.StartupCategory));
            return Task.CompletedTask;
        }

        lock (_lifecycleSync)
        {
            if (_worker is not null)
                return Task.CompletedTask;

            cancellationToken.ThrowIfCancellationRequested();
            _stopSource = new CancellationTokenSource();
            _registry.SnapshotChanged += OnSnapshotChanged;
            _worker = Task.Run(() => ProcessSignalsAsync(_stopSource.Token), CancellationToken.None);
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
            while (await _signals.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_signals.Reader.TryRead(out _))
                {
                }

                await ReconcileLatestSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            SetStatus(new AccountStateShadowStatus("worker_failed"));
        }
    }

    private async Task ReconcileLatestSnapshotAsync(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ReadOnlyCollection<AccountDescriptor> expected;
        try
        {
            var profiles = _registry.Snapshot()
                .OrderBy(profile => profile.SortOrder)
                .ThenByDescending(profile => profile.CreatedAt)
                .ToArray();
            expected = Array.AsReadOnly(profiles
                .Select((profile, index) => AccountDescriptorProjector.FromProfile(profile, index + 1))
                .ToArray());

            await using var client = await _clientFactory.ConnectCompatibleAsync(cancellationToken).ConfigureAwait(false);
            var counts = await client.ReconcileAsync(new AccountCollection(1, expected), cancellationToken).ConfigureAwait(false);
            var actual = await client.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
            var mismatches = CountMismatches(expected, actual);
            stopwatch.Stop();
            SetStatus(new AccountStateShadowStatus(
                mismatches == 0 ? "synchronized" : "mismatch",
                expected.Count,
                mismatches,
                counts.Inserted,
                counts.Updated,
                counts.Deleted,
                counts.Unchanged,
                stopwatch.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AccountStateShadowCompatibilityException)
        {
            stopwatch.Stop();
            SetStatus(new AccountStateShadowStatus("incompatible", DurationMilliseconds: stopwatch.ElapsedMilliseconds));
        }
        catch (AccountStateShadowRpcException exception) when (exception.Code == -32601)
        {
            stopwatch.Stop();
            SetStatus(new AccountStateShadowStatus("incompatible", DurationMilliseconds: stopwatch.ElapsedMilliseconds));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or AccountStateShadowUnavailableException)
        {
            stopwatch.Stop();
            SetStatus(new AccountStateShadowStatus("unavailable", DurationMilliseconds: stopwatch.ElapsedMilliseconds));
        }
        catch (Exception)
        {
            stopwatch.Stop();
            SetStatus(new AccountStateShadowStatus("sync_failed", DurationMilliseconds: stopwatch.ElapsedMilliseconds));
        }
    }

    private static int CountMismatches(IReadOnlyList<AccountDescriptor> expected, IReadOnlyList<AccountDescriptor> actual)
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

    private void SetStatus(AccountStateShadowStatus status) => Interlocked.Exchange(ref _lastStatus, status);
}
