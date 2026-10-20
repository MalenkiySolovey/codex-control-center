using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.AgentState.V1;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.AgentState.Shadow;

namespace CodexSwitcher.Core.Tests;

public sealed class AccountReadSnapshotProviderTests
{
    [Fact]
    public async Task CanaryOffAlwaysUsesLocalProjectionEvenWhenShadowIsSynchronized()
    {
        var registry = CreateLoadedRegistry(new InMemoryProfileStore([CreateProfile("Synthetic local only")]));
        var shadowSettings = EnabledShadowSettings();
        var canarySettings = AccountReadAuthorityCanarySettings.Parse("0", shadowSettings.Enabled);
        var coordinator = new AccountStateShadowCoordinator(registry, shadowSettings, new FakeFactory(), canarySettings);
        var provider = new AccountReadSnapshotProvider(registry, coordinator, canarySettings);

        await coordinator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => coordinator.LastStatus.Category == "synchronized");

        var result = provider.Read();
        Assert.Equal(AccountReadSource.Local, result.Source);
        Assert.Equal(AccountReadCanaryState.Off, result.CanaryState);
        Assert.False(result.CanaryEnabled);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void CanaryIsOptInAndFailsClosedWhenShadowIsDisabled()
    {
        var registry = CreateLoadedRegistry(new InMemoryProfileStore([CreateProfile("Synthetic local")]));
        var shadowSettings = AccountStateShadowSettings.Parse("0", null);
        var coordinator = new AccountStateShadowCoordinator(registry, shadowSettings, new FakeFactory());
        var settings = AccountReadAuthorityCanarySettings.Parse("1", shadowSettings.Enabled);
        var provider = new AccountReadSnapshotProvider(registry, coordinator, settings);

        var result = provider.Read();

        Assert.Equal(AccountReadSource.Local, result.Source);
        Assert.Equal(AccountReadCanaryState.ShadowDisabled, result.CanaryState);
        Assert.False(result.CanaryEnabled);
        Assert.Single(result.Accounts);
    }

    [Fact]
    public void OptionalPerfTraceIsBoundedAndContainsNoProfileValues()
    {
        var profile = CreateProfile("Synthetic trace secret-marker");
        profile.AccountEmail = "synthetic.trace@example.invalid";
        profile.AccountSub = "synthetic-trace-subject";
        var registry = CreateLoadedRegistry(new InMemoryProfileStore([profile]));
        var shadowSettings = AccountStateShadowSettings.Parse("0", null);
        var coordinator = new AccountStateShadowCoordinator(registry, shadowSettings, new FakeFactory());
        var canarySettings = AccountReadAuthorityCanarySettings.Parse("1", shadowSettings.Enabled);
        var perfDirectory = Path.Combine(Path.GetTempPath(), $"codex-state-canary-{Guid.NewGuid():N}");
        var diagnostics = new AccountReadAuthorityCanaryDiagnostics(perfDirectory);
        var provider = new AccountReadSnapshotProvider(registry, coordinator, canarySettings, diagnostics);

        try
        {
            _ = provider.Read();
            _ = provider.Read();
            var trace = File.ReadAllText(Directory.GetFiles(perfDirectory, "account-read-authority-*.jsonl").Single());

            Assert.Contains("ShadowDisabled", trace, StringComparison.Ordinal);
            Assert.Contains("Local", trace, StringComparison.Ordinal);
            Assert.DoesNotContain("Synthetic trace secret-marker", trace, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic.trace@example.invalid", trace, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-trace-subject", trace, StringComparison.Ordinal);
            Assert.DoesNotContain(profile.Id.ToString("D"), trace, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(perfDirectory))
                Directory.Delete(perfDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CanaryStaysLocalUntilExactReadbackThenImmediatelyFallsBackAfterMutation()
    {
        var profile = CreateProfile("Synthetic before");
        var registry = CreateLoadedRegistry(new InMemoryProfileStore([profile]));
        var shadowSettings = EnabledShadowSettings();
        var canarySettings = AccountReadAuthorityCanarySettings.Parse("1", shadowSettings.Enabled);
        var client = new FakeClient(blockCall: 2);
        var factory = new FakeFactory(client);
        var coordinator = new AccountStateShadowCoordinator(registry, shadowSettings, factory, canarySettings);
        var provider = new AccountReadSnapshotProvider(registry, coordinator, canarySettings);

        await coordinator.StartAsync(CancellationToken.None);
        await client.FirstReconcileEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => coordinator.LastStatus.Category == "synchronized");

        var active = provider.Read();
        Assert.Equal(AccountReadSource.DaemonCanary, active.Source);
        Assert.Equal(AccountReadCanaryState.DaemonActive, active.CanaryState);
        Assert.Equal(registry.CurrentGeneration, active.LastMatchedGeneration);

        registry.Rename(profile.Id, "Synthetic after");
        await client.SecondReconcileEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var fallback = provider.Read();
        Assert.Equal(AccountReadSource.Local, fallback.Source);
        Assert.Equal(AccountReadCanaryState.LocalFallbackDirty, fallback.CanaryState);
        Assert.Equal("Synthetic after", Assert.Single(fallback.Accounts).DisplayName);
        Assert.Equal(registry.CurrentGeneration, fallback.Generation);

        client.ReleaseBlockedReconcile.TrySetResult();
        await WaitUntilAsync(() => coordinator.LastStatus.Category == "synchronized" &&
                                   coordinator.LastStatus.LastMatchedGeneration == registry.CurrentGeneration);

        var resumed = provider.Read();
        Assert.Equal(AccountReadSource.DaemonCanary, resumed.Source);
        Assert.Equal("Synthetic after", Assert.Single(resumed.Accounts).DisplayName);

        await coordinator.StopAsync(CancellationToken.None);
        var stopped = provider.Read();
        Assert.Equal(AccountReadSource.Local, stopped.Source);
        Assert.Equal(AccountReadCanaryState.LocalFallbackDaemonUnavailable, stopped.CanaryState);
    }

    [Fact]
    public async Task CanaryIsPendingBeforeItsFirstExactDaemonMatch()
    {
        var registry = CreateLoadedRegistry(new InMemoryProfileStore([CreateProfile("Synthetic pending")]));
        var shadowSettings = EnabledShadowSettings();
        var canarySettings = AccountReadAuthorityCanarySettings.Parse("1", shadowSettings.Enabled);
        var client = new FakeClient(blockCall: 1);
        var coordinator = new AccountStateShadowCoordinator(registry, shadowSettings, new FakeFactory(client), canarySettings);
        var provider = new AccountReadSnapshotProvider(registry, coordinator, canarySettings);

        await coordinator.StartAsync(CancellationToken.None);
        await client.FirstReconcileEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var pending = provider.Read();
        Assert.Equal(AccountReadSource.Local, pending.Source);
        Assert.Equal(AccountReadCanaryState.PendingInitialSync, pending.CanaryState);

        client.ReleaseBlockedReconcile.TrySetResult();
        await WaitUntilAsync(() => coordinator.LastStatus.Category == "synchronized");
        Assert.Equal(AccountReadSource.DaemonCanary, provider.Read().Source);
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ThreeRapidCommitsCanOnlyActivateTheNewestGeneration()
    {
        var registry = CreateLoadedRegistry(new InMemoryProfileStore());
        var shadowSettings = EnabledShadowSettings();
        var canarySettings = AccountReadAuthorityCanarySettings.Parse("1", shadowSettings.Enabled);
        var client = new FakeClient(blockCall: 1);
        var coordinator = new AccountStateShadowCoordinator(registry, shadowSettings, new FakeFactory(client), canarySettings);
        var provider = new AccountReadSnapshotProvider(registry, coordinator, canarySettings);

        await coordinator.StartAsync(CancellationToken.None);
        await client.FirstReconcileEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        registry.Add(CreateProfile("Synthetic generation 2"));
        registry.Add(CreateProfile("Synthetic generation 3"));
        registry.Add(CreateProfile("Synthetic generation 4"));
        var newestGeneration = registry.CurrentGeneration;
        client.ReleaseBlockedReconcile.TrySetResult();

        await WaitUntilAsync(() => coordinator.LastStatus.Category == "synchronized" &&
                                   coordinator.LastStatus.LastMatchedGeneration == newestGeneration);

        Assert.Equal(newestGeneration, provider.Read().Generation);
        Assert.Equal(newestGeneration, provider.Read().LastMatchedGeneration);
        Assert.Equal(AccountReadSource.DaemonCanary, provider.Read().Source);
        Assert.Equal(2, client.Snapshots.Count);
        Assert.Equal(3, client.Snapshots.Last().Accounts.Count);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("mismatch", AccountReadCanaryState.LocalFallbackMismatch)]
    [InlineData("unavailable", AccountReadCanaryState.LocalFallbackDaemonUnavailable)]
    [InlineData("incompatible", AccountReadCanaryState.LocalFallbackProtocolIncompatible)]
    public async Task CanaryFailuresServeCurrentLocalDescriptors(string failure, AccountReadCanaryState expectedState)
    {
        var registry = CreateLoadedRegistry(new InMemoryProfileStore([CreateProfile("Synthetic fallback")]));
        var shadowSettings = EnabledShadowSettings();
        var canarySettings = AccountReadAuthorityCanarySettings.Parse("1", shadowSettings.Enabled);
        var factory = failure switch
        {
            "mismatch" => new FakeFactory(new FakeClient((_, accounts) => accounts.Select(account => account with { Health = "unknown" }).ToArray())),
            "unavailable" => new FakeFactory(connectFailure: new IOException("synthetic unavailable")),
            "incompatible" => new FakeFactory(compatibilityFailure: true),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        var coordinator = new AccountStateShadowCoordinator(registry, shadowSettings, factory, canarySettings);
        var provider = new AccountReadSnapshotProvider(registry, coordinator, canarySettings);

        await coordinator.StartAsync(CancellationToken.None);
        var expectedCategory = failure switch
        {
            "mismatch" => "mismatch",
            "unavailable" => "unavailable",
            _ => "incompatible"
        };
        await WaitUntilAsync(() => coordinator.LastStatus.Category == expectedCategory);

        var result = provider.Read();
        Assert.Equal(AccountReadSource.Local, result.Source);
        Assert.Equal(expectedState, result.CanaryState);
        Assert.Equal("Synthetic fallback", Assert.Single(result.Accounts).DisplayName);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void CanonicalProjectionPreservesEveryDescriptorField(int count)
    {
        var profiles = Enumerable.Range(0, count).Select(CreateNumberedProfile).ToArray();

        var projected = AccountStateShadowCoordinator.ProjectProfiles(profiles);
        var expected = profiles
            .OrderBy(profile => profile.SortOrder)
            .ThenByDescending(profile => profile.CreatedAt)
            .Select((profile, index) => AccountDescriptorProjector.FromProfile(profile, index + 1))
            .ToArray();

        Assert.Equal(expected, projected);
        Assert.Equal(0, AccountStateShadowCoordinator.CountMismatches(projected, expected));
        Assert.All(projected, descriptor => Assert.Equal(1, descriptor.SchemaVersion));
    }

    [Fact]
    public async Task DaemonReadbackNeverErasesFileOnlyProfileMetadata()
    {
        var profile = CreateProfile("Synthetic fidelity");
        profile.AccountEmail = "synthetic.account@example.invalid";
        profile.AccountSub = "synthetic-subject-only";
        profile.AuthMode = "chatgpt";
        profile.LastSwitchedAt = DateTimeOffset.Parse("2026-10-10T10:00:00Z", CultureInfo.InvariantCulture);
        profile.LastRefreshedAt = DateTimeOffset.Parse("2026-10-11T11:00:00Z", CultureInfo.InvariantCulture);
        profile.LastError = ErrorInfo.Create(ErrorCategory.RefreshTokenExpired, "synthetic local error", DateTimeOffset.Parse("2026-10-12T12:00:00Z", CultureInfo.InvariantCulture));
        profile.BlobFingerprint = "synthetic-fingerprint-not-a-credential";
        profile.MarkedUsedAt = DateTimeOffset.Parse("2026-10-13T13:00:00Z", CultureInfo.InvariantCulture);
        profile.SubscriptionTracking = new SubscriptionTracking(
            new DateOnly(2026, 1, 5), new DateOnly(2026, 11, 5));
        profile.DetectedSubscription = new DetectedSubscriptionInfo(
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture), DateTimeOffset.Parse("2026-12-31T00:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-10-14T14:00:00Z", CultureInfo.InvariantCulture), "synthetic-plan", DetectedSubscriptionSource.OAuthTokenClaim);

        var store = new InMemoryProfileStore([profile]);
        var registry = CreateLoadedRegistry(store);
        var shadowSettings = EnabledShadowSettings();
        var canarySettings = AccountReadAuthorityCanarySettings.Parse("1", shadowSettings.Enabled);
        var coordinator = new AccountStateShadowCoordinator(registry, shadowSettings, new FakeFactory(), canarySettings);
        var provider = new AccountReadSnapshotProvider(registry, coordinator, canarySettings);
        await coordinator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => provider.Read().Source == AccountReadSource.DaemonCanary);

        var expected = registry.Find(profile.Id)!;
        expected.Nickname = "Synthetic fidelity renamed";
        registry.Rename(profile.Id, expected.Nickname);
        var fallback = provider.Read();
        Assert.Equal(AccountReadSource.Local, fallback.Source);
        Assert.DoesNotContain("synthetic.account@example.invalid", JsonSerializer.Serialize(fallback));
        await WaitUntilAsync(() => provider.Read().Source == AccountReadSource.DaemonCanary);

        var persisted = Assert.Single(store.LoadAll());
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(persisted));
        Assert.Equal("synthetic-subject-only", persisted.AccountSub);
        Assert.Equal("chatgpt", persisted.AuthMode);
        Assert.Equal("synthetic-fingerprint-not-a-credential", persisted.BlobFingerprint);

        await coordinator.StopAsync(CancellationToken.None);
    }

    private static AccountStateShadowSettings EnabledShadowSettings() =>
        AccountStateShadowSettings.Parse("1", "C:\\synthetic\\agent-state-daemon.exe");

    private static AccountRegistry CreateLoadedRegistry(InMemoryProfileStore store)
    {
        var registry = new AccountRegistry(store, new FakeClock(), new FakeAudit());
        registry.Load();
        return registry;
    }

    private static ProfileMetadata CreateProfile(string nickname) => new()
    {
        Id = Guid.NewGuid(),
        Nickname = nickname,
        CreatedAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
        HealthStatus = HealthStatus.Valid,
        PlanType = "synthetic-plan",
        IsActive = true,
        SortOrder = 0
    };

    private static ProfileMetadata CreateNumberedProfile(int index) => new()
    {
        Id = Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
        Nickname = $"Synthetic account {index}",
        CreatedAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero).AddSeconds(index),
        HealthStatus = (index % 3) switch
        {
            0 => HealthStatus.Valid,
            1 => HealthStatus.NeedsReLogin,
            _ => HealthStatus.Unknown
        },
        PlanType = $"synthetic-plan-{index % 4}",
        IsActive = index == 2,
        SortOrder = index
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "The expected canary state was not observed before the deadline.");
    }

    private sealed class InMemoryProfileStore(IReadOnlyList<ProfileMetadata>? initial = null) : IProfileStore
    {
        private List<ProfileMetadata> _profiles = initial?.Select(Clone).ToList() ?? [];

        public List<ProfileMetadata> LoadAll() => _profiles.Select(Clone).ToList();

        public void SaveAll(IEnumerable<ProfileMetadata> profiles, ProfileSaveIntent intent = ProfileSaveIntent.NormalUpdate)
        {
            _ = intent;
            _profiles = profiles.Select(Clone).ToList();
        }

        private static ProfileMetadata Clone(ProfileMetadata profile) =>
            JsonSerializer.Deserialize<ProfileMetadata>(JsonSerializer.Serialize(profile))!;
    }

    private sealed class FakeFactory(
        FakeClient? client = null,
        Exception? connectFailure = null,
        bool compatibilityFailure = false) : IAccountStateShadowClientFactory
    {
        public FakeClient Client { get; } = client ?? new FakeClient();
        public bool IsLastConnectionHealthy { get; private set; }

        public Task<IAccountStateShadowClient> ConnectCompatibleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (connectFailure is not null)
            {
                IsLastConnectionHealthy = false;
                throw connectFailure;
            }
            if (compatibilityFailure)
            {
                IsLastConnectionHealthy = false;
                throw new AccountStateShadowCompatibilityException();
            }

            IsLastConnectionHealthy = true;
            return Task.FromResult<IAccountStateShadowClient>(Client);
        }

        public Task StopOwnedProcessAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsLastConnectionHealthy = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClient(
        Func<int, IReadOnlyList<AccountDescriptor>, IReadOnlyList<AccountDescriptor>>? readback = null,
        int blockCall = 0) : IAccountStateShadowClient
    {
        private int _callCount;
        private IReadOnlyList<AccountDescriptor> _accounts = [];

        public ConcurrentQueue<AccountCollection> Snapshots { get; } = new();
        public TaskCompletionSource<AccountCollection> FirstReconcileEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AccountCollection> SecondReconcileEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlockedReconcile { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AccountStateShadowReconcileCounts> ReconcileAsync(AccountCollection collection, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            var copy = new AccountCollection(collection.SchemaVersion, collection.Accounts.ToArray());
            Snapshots.Enqueue(copy);
            if (call == 1)
                FirstReconcileEntered.TrySetResult(copy);
            if (call == 2)
                SecondReconcileEntered.TrySetResult(copy);
            _accounts = readback?.Invoke(call, copy.Accounts) ?? copy.Accounts;

            if (blockCall == call)
                await ReleaseBlockedReconcile.Task.WaitAsync(cancellationToken);

            return new AccountStateShadowReconcileCounts(0, 0, 0, copy.Accounts.Count);
        }

        public Task<IReadOnlyList<AccountDescriptor>> ListAccountsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_accounts);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
