using System.Diagnostics;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.AgentState.V1;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.AgentState.Shadow;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using Xunit;
using Xunit.Abstractions;

namespace CodexSwitcher.AgentStateShadow.CrossRepoTests;

public sealed class RealDaemonProjectionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ProductionRegistryAndProfileStoreReconcileThroughThePinnedRealDaemon()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The real daemon integration test requires Windows named pipes.");

        var executablePath = Environment.GetEnvironmentVariable(AccountStateShadowSettings.ExecutableEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(executablePath));
        Assert.True(File.Exists(executablePath));
        var settings = AccountStateShadowSettings.Parse("1", executablePath);
        Assert.True(settings.Enabled);

        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(
            root: Path.Combine(temp.Path, "switchboard"),
            codexHome: Path.Combine(temp.Path, "codex-home"),
            legacyRoot: Path.Combine(temp.Path, "legacy-switcher"));
        appPaths.EnsureDirectories();
        var profileStore = new ProfileStore(new PhysicalFileSystem(), appPaths.ProfilesPath);
        var registry = new AccountRegistry(profileStore, new TestClock(), new NoopAuditLog());
        registry.Load();

        var pipeName = $"agent-state-daemon-test-{Guid.NewGuid():N}";
        var processManager = new AccountStateShadowDaemonProcessManager(settings, appPaths, pipeName);
        var canarySettings = AccountReadAuthorityCanarySettings.Parse("1", settings.Enabled);
        var coordinator = new AccountStateShadowCoordinator(registry, settings, processManager, canarySettings);
        var readProvider = new AccountReadSnapshotProvider(registry, coordinator, canarySettings);
        var startupTimer = Stopwatch.StartNew();
        await coordinator.StartAsync(CancellationToken.None);
        startupTimer.Stop();

        try
        {
            Assert.Equal(AccountReadSource.Local, readProvider.Read().Source);
            Assert.Equal(AccountReadCanaryState.PendingInitialSync, readProvider.Read().CanaryState);
            var initialSyncTimer = Stopwatch.StartNew();
            await WaitForProjectionAsync(pipeName, coordinator, accounts => accounts.Count == 0);
            initialSyncTimer.Stop();
            Assert.Equal(AccountReadSource.DaemonCanary, readProvider.Read().Source);
            Assert.Equal(registry.CurrentGeneration, readProvider.Read().LastMatchedGeneration);

            await processManager.StopOwnedProcessAsync(CancellationToken.None);
            var first = CreateProfile("Synthetic first", 0);
            registry.Add(first);
            var recovered = await WaitForProjectionAsync(pipeName, coordinator, accounts =>
                accounts.Count == 1 && accounts[0].AccountId == first.Id.ToString("D"));
            Assert.Equal("Synthetic first", recovered[0].DisplayName);
            Assert.Contains(profileStore.LoadAll(), profile => profile.Id == first.Id && profile.Nickname == "Synthetic first");

            var second = CreateProfile("Synthetic second", 1);
            registry.Add(second);
            var inserted = await WaitForProjectionAsync(pipeName, coordinator, accounts =>
                accounts.Count == 2 && accounts[0].AccountId == first.Id.ToString("D") && accounts[1].AccountId == second.Id.ToString("D"));
            Assert.Equal("Synthetic first", inserted[0].DisplayName);
            Assert.Equal("Synthetic second", inserted[1].DisplayName);

            registry.Rename(first.Id, "Synthetic renamed");
            Assert.Equal(AccountReadSource.Local, readProvider.Read().Source);
            Assert.Equal(AccountReadCanaryState.LocalFallbackDirty, readProvider.Read().CanaryState);
            var renamed = await WaitForProjectionAsync(pipeName, coordinator, accounts =>
                accounts.Count == 2 && accounts.Any(account => account.AccountId == first.Id.ToString("D") && account.DisplayName == "Synthetic renamed"));
            Assert.Contains(renamed, account => account.DisplayName == "Synthetic renamed");
            Assert.Equal(AccountReadSource.DaemonCanary, readProvider.Read().Source);

            registry.Reorder([second.Id, first.Id]);
            var reordered = await WaitForProjectionAsync(pipeName, coordinator, accounts =>
                accounts.Count == 2 && accounts[0].AccountId == second.Id.ToString("D") && accounts[1].AccountId == first.Id.ToString("D"));
            Assert.Equal(0, reordered[0].SortOrder);
            Assert.Equal(1, reordered[1].SortOrder);

            lock (registry.SyncRoot)
                registry.SetActiveProfileUnderLock(second.Id);
            var active = await WaitForProjectionAsync(pipeName, coordinator, accounts =>
                accounts.Count == 2 && accounts.Single(account => account.AccountId == second.Id.ToString("D")).IsActive);
            Assert.Single(active, account => account.IsActive);

            lock (registry.SyncRoot)
                registry.RemoveUnderLock(first.Id, ProfileSaveIntent.ExplicitDelete);
            var remaining = await WaitForProjectionAsync(pipeName, coordinator, accounts =>
                accounts.Count == 1 && accounts[0].AccountId == second.Id.ToString("D"));
            Assert.True(remaining[0].IsActive);

            var sizeOneIds = await ReplaceWithSyntheticCountAsync(registry, 1);
            var sizeOneTimer = Stopwatch.StartNew();
            await WaitForProjectionAsync(pipeName, coordinator, accounts => accounts.Select(account => account.AccountId).SequenceEqual(sizeOneIds));
            sizeOneTimer.Stop();
            var sizeOneSync = coordinator.LastStatus.DurationMilliseconds;

            var sizeTenIds = await ReplaceWithSyntheticCountAsync(registry, 10);
            var sizeTenTimer = Stopwatch.StartNew();
            await WaitForProjectionAsync(pipeName, coordinator, accounts => accounts.Select(account => account.AccountId).SequenceEqual(sizeTenIds));
            sizeTenTimer.Stop();
            var sizeTenSync = coordinator.LastStatus.DurationMilliseconds;

            var sizeHundredIds = await ReplaceWithSyntheticCountAsync(registry, 100);
            var sizeHundredTimer = Stopwatch.StartNew();
            await WaitForProjectionAsync(pipeName, coordinator, accounts => accounts.Select(account => account.AccountId).SequenceEqual(sizeHundredIds));
            sizeHundredTimer.Stop();
            var sizeHundredSync = coordinator.LastStatus.DurationMilliseconds;

            await processManager.StopOwnedProcessAsync(CancellationToken.None);
            Assert.Equal(AccountReadSource.Local, readProvider.Read().Source);
            Assert.Equal(AccountReadCanaryState.LocalFallbackDaemonUnavailable, readProvider.Read().CanaryState);
            await WaitForProjectionAsync(pipeName, coordinator, accounts => accounts.Select(account => account.AccountId).SequenceEqual(sizeHundredIds));
            await WaitUntilAsync(() => readProvider.Read().Source == AccountReadSource.DaemonCanary);
            var recoveredRead = readProvider.Read();
            Assert.True(recoveredRead.Source == AccountReadSource.DaemonCanary,
                $"Recovery source={recoveredRead.Source}, generation={recoveredRead.Generation}, requested={recoveredRead.LastRequestedGeneration}, matched={recoveredRead.LastMatchedGeneration}, state={recoveredRead.CanaryState}, category={coordinator.LastStatus.Category}, coordinatorGeneration={coordinator.LastStatus.CurrentGeneration}, coordinatorMatched={coordinator.LastStatus.LastMatchedGeneration}, connectionHealthy={processManager.IsLastConnectionHealthy}.");

            Assert.Equal("synchronized", coordinator.LastStatus.Category);
            Assert.Equal(0, coordinator.LastStatus.MismatchCount);
            output.WriteLine($"shadow_startup_ms={startupTimer.ElapsedMilliseconds}");
            output.WriteLine($"shadow_initial_sync_ms={initialSyncTimer.ElapsedMilliseconds}");
            output.WriteLine($"synthetic_1_total_ms={sizeOneTimer.ElapsedMilliseconds} sync_ms={sizeOneSync}");
            output.WriteLine($"synthetic_10_total_ms={sizeTenTimer.ElapsedMilliseconds} sync_ms={sizeTenSync}");
            output.WriteLine($"synthetic_100_total_ms={sizeHundredTimer.ElapsedMilliseconds} sync_ms={sizeHundredSync}");
            output.WriteLine("shadow_account_count=100 mismatch_count=0 status=synchronized");
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<IReadOnlyList<AccountDescriptor>> WaitForProjectionAsync(
        string pipeName,
        AccountStateShadowCoordinator coordinator,
        Func<IReadOnlyList<AccountDescriptor>, bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            try
            {
                await using var client = new AccountStateShadowRpcClient(pipeName);
                await client.ConnectAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None);
                var accounts = await client.ListAccountsAsync(CancellationToken.None);
                var status = coordinator.LastStatus;
                if (predicate(accounts) && status.Category == "synchronized" && status.MismatchCount == 0)
                    return accounts;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The synthetic account projection did not converge before the deadline.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (predicate())
                return;
            await Task.Delay(50);
        }

        Assert.True(predicate(), "The daemon read-authority canary did not converge before the deadline.");
    }

    private static Task<string[]> ReplaceWithSyntheticCountAsync(AccountRegistry registry, int count)
    {
        lock (registry.SyncRoot)
        {
            foreach (var existing in registry.SnapshotUnderLock())
                registry.RemoveUnderLock(existing.Id, ProfileSaveIntent.ExplicitDelete);

            var additions = Enumerable.Range(0, count)
                .Select(index => CreateProfile($"Synthetic {count}-{index}", index))
                .ToArray();
            registry.AddManyUnderLock(additions, ProfileSaveIntent.NormalUpdate);
            return Task.FromResult(additions.Select(profile => profile.Id.ToString("D")).ToArray());
        }
    }

    private static ProfileMetadata CreateProfile(string nickname, int order) => new()
    {
        Id = Guid.NewGuid(),
        Nickname = nickname,
        CreatedAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero).AddSeconds(order),
        PlanType = "synthetic",
        HealthStatus = HealthStatus.Valid,
        SortOrder = order
    };

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class NoopAuditLog : IAuditLog
    {
        public void Record(string action, string outcome, string? detail = null)
        {
            _ = action;
            _ = outcome;
            _ = detail;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-switchboard-shadow-crossrepo", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 50 && Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 49)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
