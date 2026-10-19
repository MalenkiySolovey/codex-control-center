using System.Collections.Concurrent;
using System.Text.Json;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.AgentState.V1;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.AgentState.Shadow;

namespace CodexSwitcher.Core.Tests;

public sealed class AccountStateShadowCoordinatorTests
{
    [Fact]
    public void ShadowConfigurationIsOptInAndFailsClosedForInvalidPaths()
    {
        var disabled = AccountStateShadowSettings.Parse(null, "C:\\synthetic\\daemon.exe");
        var invalid = AccountStateShadowSettings.Parse("1", "relative-daemon.exe");
        var enabled = AccountStateShadowSettings.Parse("1", "C:\\synthetic\\daemon.exe");

        Assert.False(disabled.Enabled);
        Assert.Equal("disabled", disabled.StartupCategory);
        Assert.False(invalid.Enabled);
        Assert.Equal("configuration_invalid", invalid.StartupCategory);
        Assert.True(enabled.Enabled);
    }

    [Fact]
    public async Task DisabledShadowDoesNotConnectOrStartAProcess()
    {
        var registry = CreateLoadedRegistry(new InMemoryProfileStore());
        var factory = new FakeClientFactory();
        var coordinator = new AccountStateShadowCoordinator(
            registry,
            AccountStateShadowSettings.Parse("0", null),
            factory);

        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(0, factory.ConnectCount);
        Assert.Equal("disabled", coordinator.LastStatus.Category);
    }

    [Fact]
    public async Task CommittedChangesCoalesceAndOnlyOneReconcileRunsAtATime()
    {
        var registry = CreateLoadedRegistry(new InMemoryProfileStore());
        var client = new FakeClient(blockFirstReconcile: true);
        var factory = new FakeClientFactory(client);
        var coordinator = CreateEnabledCoordinator(registry, factory);

        await coordinator.StartAsync(CancellationToken.None);
        var initial = await client.FirstReconcileEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(initial.Accounts);

        registry.Add(CreateProfile("Synthetic one"));
        registry.Add(CreateProfile("Synthetic two"));
        client.ReleaseFirstReconcile.TrySetResult();

        var latest = await client.SecondReconcileEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, latest.Accounts.Count);
        Assert.Equal(1, client.MaximumConcurrentReconciles);
        await WaitUntilAsync(() => coordinator.LastStatus.Category == "synchronized");

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ProjectionExcludesCredentialFieldsAndMismatchStatusIsSanitized()
    {
        var profile = CreateProfile("Safe synthetic display");
        profile.AccountEmail = "private-marker@example.invalid";
        profile.AccountSub = "private-account-sub-marker";
        profile.BlobFingerprint = "private-fingerprint-marker";
        profile.AuthMode = "private-auth-mode-marker";
        var registry = CreateLoadedRegistry(new InMemoryProfileStore([profile]));
        var client = new FakeClient(account => [account with { DisplayName = "different synthetic display" }]);
        var coordinator = CreateEnabledCoordinator(registry, new FakeClientFactory(client));

        await coordinator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.LastStatus.Category == "mismatch");
        var projected = Assert.Single(client.Snapshots);
        var serializedProjection = JsonSerializer.Serialize(projected);
        var serializedStatus = JsonSerializer.Serialize(coordinator.LastStatus);

        Assert.Equal("Safe synthetic display", projected.Accounts[0].DisplayName);
        Assert.Equal(1, coordinator.LastStatus.AccountCount);
        Assert.Equal(1, coordinator.LastStatus.MismatchCount);
        Assert.DoesNotContain("private-marker@example.invalid", serializedProjection, StringComparison.Ordinal);
        Assert.DoesNotContain("private-account-sub-marker", serializedProjection, StringComparison.Ordinal);
        Assert.DoesNotContain("private-fingerprint-marker", serializedProjection, StringComparison.Ordinal);
        Assert.DoesNotContain("private-auth-mode-marker", serializedProjection, StringComparison.Ordinal);
        Assert.DoesNotContain("Safe synthetic display", serializedStatus, StringComparison.Ordinal);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IncompatibleDaemonIsClassifiedWithoutSendingAccountMetadata()
    {
        var registry = CreateLoadedRegistry(new InMemoryProfileStore([CreateProfile("Synthetic only")]));
        var factory = new FakeClientFactory(throwCompatibility: true);
        var coordinator = CreateEnabledCoordinator(registry, factory);

        await coordinator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.LastStatus.Category == "incompatible");

        Assert.Equal(0, factory.Client.ReconcileCount);
        Assert.Equal(0, factory.Client.ListCount);
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("missing-executable", "unavailable")]
    [InlineData("process-exited-before-pipe-ready", "unavailable")]
    [InlineData("pipe-timeout", "unavailable")]
    [InlineData("malformed-response", "incompatible")]
    [InlineData("wrong-manifest-hash", "incompatible")]
    [InlineData("method-not-found", "incompatible")]
    [InlineData("shadow-database-busy", "sync_failed")]
    public async Task ShadowFailuresNeverBlockProductionProfilePersistence(string failure, string expectedCategory)
    {
        var store = new InMemoryProfileStore();
        var registry = CreateLoadedRegistry(store);
        var factory = new FakeClientFactory(connectFailure: CreateFailure(failure));
        var coordinator = CreateEnabledCoordinator(registry, factory);

        await coordinator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.LastStatus.Category == expectedCategory);

        var added = registry.Add(CreateProfile("Production save survives shadow failure"));

        var persisted = Assert.Single(store.LoadAll());
        Assert.Equal(added.Id, persisted.Id);
        Assert.Equal("Production save survives shadow failure", persisted.Nickname);
        await WaitUntilAsync(() => coordinator.LastStatus.Category == expectedCategory);
        await coordinator.StopAsync(CancellationToken.None);
    }

    private static Exception CreateFailure(string failure) => failure switch
    {
        "missing-executable" => new AccountStateShadowUnavailableException(),
        "process-exited-before-pipe-ready" => new IOException(),
        "pipe-timeout" => new TimeoutException(),
        "malformed-response" => new AccountStateShadowCompatibilityException(),
        "wrong-manifest-hash" => new AccountStateShadowCompatibilityException(),
        "method-not-found" => new AccountStateShadowRpcException(-32601),
        "shadow-database-busy" => new AccountStateShadowRpcException(-32001),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    private static AccountRegistry CreateLoadedRegistry(IProfileStore store)
    {
        var registry = new AccountRegistry(store, new FakeClock(), new FakeAudit());
        registry.Load();
        return registry;
    }

    private static AccountStateShadowCoordinator CreateEnabledCoordinator(
        AccountRegistry registry,
        IAccountStateShadowClientFactory factory) => new(
            registry,
            AccountStateShadowSettings.Parse("1", "C:\\synthetic\\agent-state-daemon.exe"),
            factory);

    private static ProfileMetadata CreateProfile(string nickname) => new()
    {
        Id = Guid.NewGuid(),
        Nickname = nickname,
        DetectedDisplayName = "synthetic detected name",
        CreatedAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
        PlanType = "synthetic-plan",
        IsActive = true,
        SortOrder = 0
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "The expected shadow coordinator state was not observed before the deadline.");
    }

    private sealed class InMemoryProfileStore(IReadOnlyList<ProfileMetadata>? initial = null) : IProfileStore
    {
        private List<ProfileMetadata> _profiles = initial?.ToList() ?? [];

        public List<ProfileMetadata> LoadAll() => _profiles.ToList();

        public void SaveAll(IEnumerable<ProfileMetadata> profiles, ProfileSaveIntent intent = ProfileSaveIntent.NormalUpdate)
        {
            _ = intent;
            _profiles = profiles.ToList();
        }
    }

    private sealed class FakeClientFactory(
        FakeClient? client = null,
        bool throwCompatibility = false,
        Exception? connectFailure = null) : IAccountStateShadowClientFactory
    {
        public FakeClient Client { get; } = client ?? new FakeClient();
        public int ConnectCount { get; private set; }

        public Task<IAccountStateShadowClient> ConnectCompatibleAsync(CancellationToken cancellationToken)
        {
            ConnectCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (throwCompatibility)
                throw new AccountStateShadowCompatibilityException();
            if (connectFailure is not null)
                throw connectFailure;
            return Task.FromResult<IAccountStateShadowClient>(Client);
        }

        public Task StopOwnedProcessAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClient : IAccountStateShadowClient
    {
        private readonly Func<AccountDescriptor, IReadOnlyList<AccountDescriptor>>? _readbackProjection;
        private int _inFlight;
        private int _reconcileCount;
        private IReadOnlyList<AccountDescriptor> _readback = [];

        public FakeClient(bool blockFirstReconcile = false)
        {
            BlockFirstReconcile = blockFirstReconcile;
        }

        public FakeClient(Func<AccountDescriptor, IReadOnlyList<AccountDescriptor>> readbackProjection)
        {
            _readbackProjection = readbackProjection;
        }

        public bool BlockFirstReconcile { get; }
        public int MaximumConcurrentReconciles { get; private set; }
        public int ReconcileCount => Volatile.Read(ref _reconcileCount);
        public int ListCount { get; private set; }
        public ConcurrentQueue<AccountCollection> Snapshots { get; } = new();
        public TaskCompletionSource<AccountCollection> FirstReconcileEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AccountCollection> SecondReconcileEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstReconcile { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AccountStateShadowReconcileCounts> ReconcileAsync(AccountCollection collection, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _inFlight);
            MaximumConcurrentReconciles = Math.Max(MaximumConcurrentReconciles, active);
            var call = Interlocked.Increment(ref _reconcileCount);
            var snapshot = new AccountCollection(collection.SchemaVersion, collection.Accounts.ToArray());
            Snapshots.Enqueue(snapshot);
            FirstReconcileEntered.TrySetResult(snapshot);
            if (call >= 2)
                SecondReconcileEntered.TrySetResult(snapshot);

            _readback = _readbackProjection is null
                ? snapshot.Accounts
                : snapshot.Accounts.SelectMany(_readbackProjection).ToArray();

            try
            {
                if (BlockFirstReconcile && call == 1)
                    await ReleaseFirstReconcile.Task.WaitAsync(cancellationToken);
                return new AccountStateShadowReconcileCounts(0, 0, 0, snapshot.Accounts.Count);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<IReadOnlyList<AccountDescriptor>> ListAccountsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCount++;
            return Task.FromResult(_readback);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
