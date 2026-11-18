using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.AgentState.V1;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Infra.AgentState.Shadow;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using System.IO.Pipes;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class NicknameAuthorityStabilizationTests
{
    [Fact]
    public async Task VerifiedRemoteRenameWithMirrorSaveFailureIsReportedAndRepaired()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.BeforeNextUpdate = () =>
        {
            fixture.Store.FailNextSave = true;
            fixture.Factory.BlockNextConnect();
        };

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Remote accepted");
        await fixture.Factory.WaitUntilBlockedAsync();

        Assert.Equal(AccountNicknameMutationOutcome.DaemonAppliedCachePending, result.Outcome);
        Assert.Equal("Remote accepted", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
        Assert.Equal("Original", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal("nickname_source_sync_pending", fixture.Coordinator.LastStatus.Category);

        fixture.Factory.ReleaseBlockedConnect();
        await WaitUntilAsync(() => fixture.Registry.Find(fixture.Profile.Id)?.Nickname == "Remote accepted" &&
            fixture.Coordinator.LastStatus.Category == "nickname_source_synchronized");
        Assert.Equal("Remote accepted", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
        Assert.Equal("nickname_source_synchronized", fixture.Coordinator.LastStatus.Category);
    }

    [Fact]
    public async Task RestartWithStaleMirrorRefreshesFromAvailableDaemon()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.SetExternalNickname(fixture.Profile.Id, "Latest daemon value");
        var generation = fixture.Registry.CurrentGeneration;
        Assert.True(fixture.Registry.UpdateNicknameMirrorsIfGeneration(
            new Dictionary<Guid, string?> { [fixture.Profile.Id] = "Stale local cache" }, generation));

        await fixture.RestartServiceAsync();

        Assert.Equal("Latest daemon value", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal("Latest daemon value", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
    }

    [Fact]
    public async Task RestartOfflineKeepsReadOnlyCacheAndDoesNotRespawnOrWriteLocally()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.IsAvailable = false;
        var generation = fixture.Registry.CurrentGeneration;
        Assert.True(fixture.Registry.UpdateNicknameMirrorsIfGeneration(
            new Dictionary<Guid, string?> { [fixture.Profile.Id] = "Read-only cache" }, generation));

        await fixture.RestartServiceAsync();
        Assert.True(fixture.Service.IsAuthorityPotentiallyActive);
        Assert.Equal("Read-only cache", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        var connectCountAfterStartup = fixture.Factory.ConnectCount;

        fixture.Registry.UpdateUnderLock(fixture.Profile.Id, profile => profile.SortOrder = 8);
        await WaitUntilAsync(() => fixture.Coordinator.LastStatus.Category == "nickname_source_sync_pending");
        await Task.Delay(150);
        Assert.Equal(connectCountAfterStartup, fixture.Factory.ConnectCount);

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Must stay read-only");
        Assert.Equal(AccountNicknameMutationOutcome.ProvenNotApplied, result.Outcome);
        Assert.Equal("Read-only cache", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal(0, fixture.Client.UpdateCount);
        await Task.Delay(150);
        Assert.Equal(connectCountAfterStartup + 1, fixture.Factory.ConnectCount);

        fixture.Client.IsAvailable = true;
        await fixture.Service.SynchronizeActiveSourceAsync();
        Assert.Equal("nickname_source_synchronized", fixture.Coordinator.LastStatus.Category);
        Assert.Equal(8, fixture.Client.CurrentSource!.SortOrder);
        Assert.Equal("Original", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
    }

    [Fact]
    public async Task NewerSourceSnapshotsDuringReconcileAreCoalescedAndNicknameIsPreserved()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.BlockNextSourceReconcile();

        fixture.Registry.UpdateUnderLock(fixture.Profile.Id, profile => profile.SortOrder = 5);
        await fixture.Client.WaitUntilSourceReconcileBlockedAsync();
        fixture.Registry.UpdateUnderLock(fixture.Profile.Id, profile =>
        {
            profile.SortOrder = 6;
            profile.HealthStatus = HealthStatus.Unknown;
        });
        fixture.Registry.UpdateUnderLock(fixture.Profile.Id, profile =>
        {
            profile.SortOrder = 7;
            profile.IsActive = false;
        });
        fixture.Registry.UpdateUnderLock(fixture.Profile.Id, profile =>
        {
            profile.SortOrder = 9;
            profile.IsActive = false;
            profile.HealthStatus = HealthStatus.NeedsReLogin;
        });
        fixture.Client.ReleaseBlockedSourceReconcile();

        await WaitUntilAsync(() => fixture.Client.CurrentSource is { } source &&
            source.SortOrder == 9 && !source.IsActive && source.Health == "needs_relogin" &&
            fixture.Coordinator.LastStatus.Category == "nickname_source_synchronized");

        Assert.Equal("Original", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
        Assert.Equal(9, fixture.Client.CurrentSource!.SortOrder);
    }

    [Fact]
    public async Task SingleSourceUpdateConvergesToItsCommittedProjection()
    {
        await using var fixture = await Fixture.CreateAsync();

        fixture.Registry.UpdateUnderLock(fixture.Profile.Id, profile => profile.SortOrder = 4);
        await WaitUntilAsync(() => fixture.Coordinator.LastStatus.Category == "nickname_source_synchronized" &&
            fixture.Client.CurrentSource?.SortOrder == 4);

        Assert.Equal("Original", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
        Assert.Equal(4, fixture.Client.CurrentSource!.SortOrder);
    }

    [Fact]
    public async Task PersistentSourceReconcileFailureLeavesSanitizedPendingStatusAndStopsRetrying()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.SourceReconcileFailuresRemaining = 20;
        var connectsBefore = fixture.Factory.ConnectCount;

        fixture.Registry.UpdateUnderLock(fixture.Profile.Id, profile => profile.SortOrder = 11);
        await WaitUntilAsync(() => fixture.Coordinator.LastStatus.Category == "nickname_source_sync_pending");
        await Task.Delay(150);

        Assert.Equal("nickname_source_sync_pending", fixture.Coordinator.LastStatus.Category);
        Assert.InRange(fixture.Factory.ConnectCount - connectsBefore, 0, 1);
        Assert.DoesNotContain("Synthetic base", System.Text.Json.JsonSerializer.Serialize(fixture.Coordinator.LastStatus), StringComparison.Ordinal);
        Assert.Equal("Original", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
    }

    [Fact]
    public async Task AbandonedDaemonStartupLeaseCanBeRecoveredAfterThreadExit()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "cc-daemon-startup-abandoned", Guid.NewGuid().ToString("N"));
        var mutexName = DaemonStartupLease.GetMutexName(dataDirectory);
        using var acquired = new ManualResetEventSlim();
        Exception? workerException = null;
        Mutex? abandonedMutex = null;
        var owner = new Thread(() =>
        {
            try
            {
                var options = new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true };
                abandonedMutex = new Mutex(false, mutexName, options, out _);
                abandonedMutex.WaitOne();
                acquired.Set();
            }
            catch (Exception exception)
            {
                workerException = exception;
                acquired.Set();
            }
        })
        { IsBackground = true };
        owner.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(workerException);

        try
        {
            await using var lease = await DaemonStartupLease.AcquireAsync(dataDirectory, CancellationToken.None);
            Assert.True(lease.WasAbandoned);
        }
        finally
        {
            abandonedMutex!.Dispose();
        }
    }

    [Fact]
    public async Task ExplicitRevisionConflictNeverBecomesAFalseNicknameSuccess()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.BeforeNextUpdate = fixture.Client.AdvanceSourceRevisionWithoutChangingNickname;

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Should conflict");

        Assert.Equal(AccountNicknameMutationOutcome.Conflict, result.Outcome);
        Assert.Equal("Original", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
        Assert.Equal("Original", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal(1, fixture.Client.UpdateCount);
    }

    [Fact]
    public async Task RemoteMutationFailureBeforeCommitDoesNotReportSuccess()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.NextUpdateFault = UpdateFault.ExplicitFailureBeforeCommit;

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Must not be committed");

        Assert.Equal(AccountNicknameMutationOutcome.ProvenNotApplied, result.Outcome);
        Assert.Equal("Original", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
        Assert.Equal("Original", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal(1, fixture.Client.UpdateCount);
    }

    [Fact]
    public async Task RpcDisconnectAfterNicknameRequestWasSentIsClassifiedAsUnknown()
    {
        var pipeName = $"nickname-authority-fault-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        var receiveAndClose = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var buffer = new byte[256];
            while (true)
            {
                var read = await server.ReadAsync(buffer);
                if (read == 0 || Array.IndexOf(buffer, (byte)'\n', 0, read) >= 0)
                {
                    await server.DisposeAsync();
                    return;
                }
            }
        });

        await using var client = new AccountStateShadowRpcClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        var mutation = new AccountNicknameMutation
        {
            AccountId = "synthetic-account",
            Nickname = "synthetic desired",
            SafeFallbackDisplayName = "Account",
            ExpectedRevision = "synthetic-revision"
        };

        await Assert.ThrowsAsync<AccountStateShadowRpcOutcomeUnknownException>(() =>
            client.UpdateNicknameAsync(mutation, 3, "synthetic-migration", CancellationToken.None));
        await receiveAndClose.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RpcFailureBeforeConnectIsNotMisclassifiedAsUnknown()
    {
        await using var client = new AccountStateShadowRpcClient($"nickname-authority-offline-{Guid.NewGuid():N}");
        var mutation = new AccountNicknameMutation
        {
            AccountId = "synthetic-account",
            Nickname = "synthetic desired",
            SafeFallbackDisplayName = "Account",
            ExpectedRevision = "synthetic-revision"
        };

        await Assert.ThrowsAsync<AccountStateShadowRpcRequestNotSentException>(() =>
            client.UpdateNicknameAsync(mutation, 3, "synthetic-migration", CancellationToken.None));
    }

    [Theory]
    [InlineData(UpdateFault.TransportLostBeforeCommit)]
    [InlineData(UpdateFault.TransportLostAfterCommit)]
    [InlineData(UpdateFault.TransportLostThenChangedByAnotherClient)]
    public async Task LostMutationResponseIsUnknownOrConflictedWithoutStaleRetry(UpdateFault fault)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.NextUpdateFault = fault;

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Request lost");

        var expected = fault switch
        {
            UpdateFault.TransportLostBeforeCommit => AccountNicknameMutationOutcome.ProvenNotApplied,
            UpdateFault.TransportLostThenChangedByAnotherClient => AccountNicknameMutationOutcome.Conflict,
            _ => AccountNicknameMutationOutcome.OutcomeUnknown
        };
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(1, fixture.Client.UpdateCount);
        if (fault == UpdateFault.TransportLostAfterCommit)
            Assert.Equal("Request lost", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
        if (fault == UpdateFault.TransportLostThenChangedByAnotherClient)
            Assert.Equal("Concurrent winner", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
        Assert.Equal("Original", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
    }

    [Fact]
    public async Task FailureBeforeNicknameRequestIsSentIsReportedAsProvenNotApplied()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.IsAvailable = false;

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Never sent");

        Assert.Equal(AccountNicknameMutationOutcome.ProvenNotApplied, result.Outcome);
        Assert.Equal(0, fixture.Client.UpdateCount);
        Assert.Equal("Original", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
    }

    [Fact]
    public void ExistingAccountUiHasDistinctLocalizedReportsForNicknameAuthorityOutcomes()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModel = File.ReadAllText(Path.Combine(repository, "src", "CodexSwitcher.App", "Features", "Accounts", "AccountsViewModel.cs"));
        var strings = File.ReadAllText(Path.Combine(repository, "src", "CodexSwitcher.App", "Localization", "Strings.cs"));

        Assert.Contains("case AccountNicknameMutationOutcome.DaemonAppliedCachePending:", viewModel, StringComparison.Ordinal);
        Assert.Contains("Loc.NicknameCacheRepairPendingMsg", viewModel, StringComparison.Ordinal);
        Assert.Contains("case AccountNicknameMutationOutcome.OutcomeUnknown:", viewModel, StringComparison.Ordinal);
        Assert.Contains("Loc.NicknameOutcomeUnknownMsg", viewModel, StringComparison.Ordinal);
        Assert.Contains("AccountNicknameMutationOutcome.ProvenNotApplied => (Loc.NicknameUpdateFailedTitle, Loc.NicknameNotAppliedMsg, InfoBarSeverity.Warning)", viewModel, StringComparison.Ordinal);
        Assert.Contains("NicknameCacheRepairPendingMsg => S(", strings, StringComparison.Ordinal);
        Assert.Contains("NicknameOutcomeUnknownMsg => S(", strings, StringComparison.Ordinal);
        Assert.Contains("NicknameNotAppliedMsg => S(", strings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOutcomeWithUnavailableDaemonRepairsFromFreshStateAfterReconnect()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.NextUpdateFault = UpdateFault.TransportLostAfterCommitAndDisconnect;

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Committed before disconnect");

        Assert.Equal(AccountNicknameMutationOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal("Original", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal(1, fixture.Client.UpdateCount);

        fixture.Client.IsAvailable = true;
        await fixture.Service.SynchronizeActiveSourceAsync();

        Assert.Equal("Committed before disconnect", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal("Committed before disconnect", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
    }

    [Fact]
    public async Task NewerDaemonNicknameWinsOverStaleMirrorRepair()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.BeforeNextUpdate = () =>
        {
            fixture.Store.FailNextSave = true;
            fixture.Factory.BlockNextConnect();
        };

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Pending value");
        await fixture.Factory.WaitUntilBlockedAsync();
        Assert.Equal(AccountNicknameMutationOutcome.DaemonAppliedCachePending, result.Outcome);

        fixture.Client.SetExternalNickname(fixture.Profile.Id, "Newer daemon value");
        fixture.Factory.ReleaseBlockedConnect();
        await WaitUntilAsync(() => fixture.Registry.Find(fixture.Profile.Id)?.Nickname == "Newer daemon value");

        Assert.Equal("Newer daemon value", fixture.Client.GetCurrentNickname(fixture.Profile.Id));
    }

    [Fact]
    public async Task MirrorCacheCommitDoesNotEmitAnotherSourceSnapshotSignal()
    {
        var store = new MemoryProfileStore();
        var registry = new AccountRegistry(store, new TestClock(), new NoopAuditLog());
        registry.Load();
        var profile = CreateProfile("Original");
        registry.Add(profile);
        var generation = registry.CurrentGeneration;
        var notifications = 0;
        registry.SnapshotChanged += () => notifications++;

        Assert.True(registry.UpdateNicknameMirrorsIfGeneration(new Dictionary<Guid, string?> { [profile.Id] = "Verified" }, generation));

        Assert.Equal(0, notifications);
        Assert.Equal(generation + 1, registry.CurrentGeneration);
        Assert.True(registry.VerifyPersistedNicknameMirrors(new Dictionary<Guid, string?> { [profile.Id] = "Verified" }));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(predicate(), "The expected synthetic nickname-authority state was not observed.");
    }

    private static ProfileMetadata CreateProfile(string nickname) => new()
    {
        Id = Guid.Parse("01234567-89ab-4cde-8123-0123456789ab"),
        Nickname = nickname,
        DetectedDisplayName = "Synthetic base",
        AccountSub = "synthetic-nickname-authority",
        CreatedAt = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero),
        HealthStatus = HealthStatus.Valid,
        IsActive = true,
        SortOrder = 0
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string path,
            AppPaths paths,
            MemoryProfileStore store,
            AccountRegistry registry,
            ProfileMetadata profile,
            NicknameAuthorityMigrationJournal journal,
            FakeClient client,
            FakeFactory factory,
            AccountTransferService transfer,
            AccountStateShadowCoordinator coordinator,
            AccountNicknameAuthorityService service)
        {
            TempPath = path;
            Paths = paths;
            Store = store;
            Registry = registry;
            Profile = profile;
            Journal = journal;
            Client = client;
            Factory = factory;
            Transfer = transfer;
            Coordinator = coordinator;
            Service = service;
        }

        public string TempPath { get; }
        public AppPaths Paths { get; }
        public MemoryProfileStore Store { get; }
        public AccountRegistry Registry { get; }
        public ProfileMetadata Profile { get; }
        public NicknameAuthorityMigrationJournal Journal { get; }
        public FakeClient Client { get; }
        public FakeFactory Factory { get; }
        public AccountTransferService Transfer { get; }
        public AccountStateShadowCoordinator Coordinator { get; private set; }
        public AccountNicknameAuthorityService Service { get; private set; }

        public static async Task<Fixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), "cc-nickname-stabilization", Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(path, "switchboard"), Path.Combine(path, "codex-home"), Path.Combine(path, "legacy"));
            paths.EnsureDirectories();
            var store = new MemoryProfileStore();
            var registry = new AccountRegistry(store, new TestClock(), new NoopAuditLog());
            registry.Load();
            var profile = CreateProfile("Original");
            registry.Add(profile);
            var journal = new NicknameAuthorityMigrationJournal(new PhysicalFileSystem(), paths);
            journal.Save(new NicknameAuthorityJournal
            {
                AuthorityState = NicknameAuthorityStates.DaemonNicknameAuthority,
                AuthorityEpoch = 3,
                MigrationId = "synthetic-authority-migration",
                SourceGeneration = registry.CurrentGeneration,
                AuthorityWasActivated = true,
                Rows = [new NicknameAuthorityMigrationRow
                {
                    AccountId = profile.Id.ToString("D"),
                    DaemonRevision = "revision-1",
                    Phase = "active"
                }]
            });
            var client = new FakeClient(profile);
            var factory = new FakeFactory(client);
            var shadowSettings = AccountStateShadowSettings.Parse("1", "C:\\synthetic\\agent-state-daemon.exe");
            var authoritySettings = NicknameAuthorityCanarySettings.Parse("1", "C:\\synthetic\\agent-state-daemon.exe");
            var coordinator = new AccountStateShadowCoordinator(
                registry, shadowSettings, factory,
                nicknameAuthoritySettings: authoritySettings,
                migrationJournal: journal);
            var transfer = new AccountTransferService(
                registry,
                new AccountCredentialService(new SyntheticVault(), registry, new TestClock(), new NoopAuditLog()),
                new NoopAuditLog());
            var service = new AccountNicknameAuthorityService(registry, transfer, authoritySettings, journal, coordinator, factory, paths);
            await service.InitializeAsync();
            Assert.True(service.IsAuthorityActive);
            return new Fixture(path, paths, store, registry, profile, journal, client, factory, transfer, coordinator, service);
        }

        public async Task RestartServiceAsync()
        {
            Service.Dispose();
            Coordinator.Dispose();
            var shadowSettings = AccountStateShadowSettings.Parse("1", "C:\\synthetic\\agent-state-daemon.exe");
            var authoritySettings = NicknameAuthorityCanarySettings.Parse("1", "C:\\synthetic\\agent-state-daemon.exe");
            Coordinator = new AccountStateShadowCoordinator(
                Registry, shadowSettings, Factory,
                nicknameAuthoritySettings: authoritySettings,
                migrationJournal: Journal);
            Service = new AccountNicknameAuthorityService(Registry, Transfer, authoritySettings, Journal, Coordinator, Factory, Paths);
            await Service.InitializeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            Coordinator.Dispose();
            for (var attempt = 0; attempt < 15 && Directory.Exists(TempPath); attempt++)
            {
                try
                {
                    Directory.Delete(TempPath, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 14)
                {
                    await Task.Delay(20);
                }
            }
        }
    }

    private sealed class FakeFactory(FakeClient client) : IAccountStateShadowClientFactory
    {
        private readonly object _sync = new();
        private TaskCompletionSource? _connectEntered;
        private TaskCompletionSource? _connectRelease;
        private bool _connectBlockArmed;
        private int _connectCount;

        public bool IsLastConnectionHealthy => client.IsAvailable;
        public int ConnectCount => Volatile.Read(ref _connectCount);

        public void BlockNextConnect()
        {
            lock (_sync)
            {
                _connectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _connectRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _connectBlockArmed = true;
            }
        }

        public Task WaitUntilBlockedAsync()
        {
            lock (_sync)
                return (_connectEntered ?? throw new InvalidOperationException("No connect block was armed.")).Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void ReleaseBlockedConnect()
        {
            lock (_sync)
                (_connectRelease ?? throw new InvalidOperationException("No connect block was armed.")).TrySetResult();
        }

        public Task<IAccountStateShadowClient> ConnectCompatibleAsync(CancellationToken cancellationToken) => ConnectAsync(cancellationToken);

        public Task<IAccountStateShadowClient> ConnectNicknameAuthorityCompatibleAsync(CancellationToken cancellationToken) => ConnectAsync(cancellationToken);

        public async Task<IAccountStateShadowClient?> TryConnectExistingNicknameAuthorityAsync(CancellationToken cancellationToken)
        {
            if (!client.IsAvailable)
                return null;
            return await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<IAccountStateShadowClient> ConnectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            TaskCompletionSource? entered;
            TaskCompletionSource? release;
            lock (_sync)
            {
                entered = _connectBlockArmed ? _connectEntered : null;
                release = _connectBlockArmed ? _connectRelease : null;
                _connectBlockArmed = false;
            }
            if (entered is not null && release is not null)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    if (ReferenceEquals(_connectRelease, release))
                    {
                        _connectEntered = null;
                        _connectRelease = null;
                    }
                }
            }
            if (!client.IsAvailable)
                throw new AccountStateShadowUnavailableException();
            return client;
        }

        public Task StopOwnedProcessAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeClient : IAccountStateShadowClient
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, AccountNicknameState> _nicknames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AccountSourceDescriptor> _source = new(StringComparer.Ordinal);
        private int _revision = 1;
        private int _updateCount;
        private TaskCompletionSource? _reconcileEntered;
        private TaskCompletionSource? _reconcileRelease;
        private bool _reconcileBlockArmed;
        private Action? _beforeNextUpdate;
        private Action? _beforeNextNicknameRead;

        public FakeClient(ProfileMetadata profile)
        {
            var accountId = profile.Id.ToString("D");
            _nicknames.Add(accountId, new AccountNicknameState { AccountId = accountId, Nickname = profile.Nickname, Revision = "revision-1" });
        }

        public bool SupportsNicknameAuthority => true;
        public bool SupportsNicknameMutation => true;
        public bool IsAvailable { get; set; } = true;
        public int UpdateCount => Volatile.Read(ref _updateCount);
        public UpdateFault NextUpdateFault { get; set; }
        public int SourceReconcileFailuresRemaining { get; set; }
        public Action? BeforeNextUpdate
        {
            get => Volatile.Read(ref _beforeNextUpdate);
            set => Volatile.Write(ref _beforeNextUpdate, value);
        }
        public AccountSourceDescriptor? CurrentSource
        {
            get
            {
                lock (_sync)
                    return _source.Values.SingleOrDefault();
            }
        }

        public void BlockNextSourceReconcile()
        {
            lock (_sync)
            {
                _reconcileEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _reconcileRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _reconcileBlockArmed = true;
            }
        }

        public Task WaitUntilSourceReconcileBlockedAsync()
        {
            lock (_sync)
                return (_reconcileEntered ?? throw new InvalidOperationException("No reconcile block was armed.")).Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void ReleaseBlockedSourceReconcile()
        {
            lock (_sync)
                (_reconcileRelease ?? throw new InvalidOperationException("No reconcile block was armed.")).TrySetResult();
        }

        public string? GetCurrentNickname(Guid accountId)
        {
            lock (_sync)
                return _nicknames[accountId.ToString("D")].Nickname;
        }

        public void SetExternalNickname(Guid accountId, string nickname)
        {
            lock (_sync)
            {
                var id = accountId.ToString("D");
                var state = _nicknames[id];
                _nicknames[id] = state with { Nickname = nickname, Revision = NextRevision() };
            }
        }

        public void AdvanceSourceRevisionWithoutChangingNickname()
        {
            lock (_sync)
            {
                var id = Assert.Single(_nicknames.Keys);
                _nicknames[id] = _nicknames[id] with { Revision = NextRevision() };
            }
        }

        public Task<AccountStateShadowReconcileCounts> ReconcileAsync(AccountCollection collection, CancellationToken cancellationToken) =>
            Task.FromResult(new AccountStateShadowReconcileCounts(0, 0, 0, collection.Accounts.Count));

        public Task<IReadOnlyList<AccountDescriptor>> ListAccountsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailable();
            lock (_sync)
                return Task.FromResult<IReadOnlyList<AccountDescriptor>>(_source.Values.Select(Project).ToArray());
        }

        public Task<AuthorityDaemonInfo> GetAuthorityDaemonInfoAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailable();
            var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["shadow.accounts.reconcile"] = false,
                ["accounts.updateMetadata"] = false,
                ["accounts.getNickname"] = true,
                ["accounts.listNicknames"] = true,
                ["accounts.updateNickname"] = true,
                ["shadow.accounts.reconcileSource"] = true,
                ["accounts.nicknameAuthority.get"] = true,
                ["accounts.nicknameAuthority.lifecycle"] = true
            };
            return Task.FromResult(new AuthorityDaemonInfo(2, NicknameAuthorityStates.DaemonNicknameAuthority, true, capabilities));
        }

        public Task<NicknameAuthorityStatus> GetNicknameAuthorityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailable();
            return Task.FromResult(new NicknameAuthorityStatus
            {
                State = NicknameAuthorityStates.DaemonNicknameAuthority,
                Epoch = 3,
                MigrationId = "synthetic-authority-migration",
                ProtocolManifestSha256 = AccountStateShadowRpcClient.ProtocolManifestSha256
            });
        }

        public Task<AccountNicknameCollection> ListNicknamesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailable();
            lock (_sync)
                return Task.FromResult(new AccountNicknameCollection { Accounts = _nicknames.Values.ToArray() });
        }

        public Task<AccountNicknameState> GetNicknameAsync(string accountId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailable();
            Interlocked.Exchange(ref _beforeNextNicknameRead, null)?.Invoke();
            lock (_sync)
                return Task.FromResult(_nicknames[accountId]);
        }

        public async Task<NicknameMutationResult> UpdateNicknameAsync(
            AccountNicknameMutation mutation,
            long expectedAuthorityEpoch,
            string? migrationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailable();
            Interlocked.Increment(ref _updateCount);
            Interlocked.Exchange(ref _beforeNextUpdate, null)?.Invoke();
            var fault = NextUpdateFault;
            NextUpdateFault = UpdateFault.None;
            if (fault == UpdateFault.TransportLostBeforeCommit)
                throw new AccountStateShadowRpcOutcomeUnknownException();
            if (fault == UpdateFault.ExplicitFailureBeforeCommit)
                throw new AccountStateShadowRpcException(-32000, "storage_failure");

            AccountNicknameState updated;
            AccountDescriptor account;
            lock (_sync)
            {
                var current = _nicknames[mutation.AccountId];
                if (current.Revision != mutation.ExpectedRevision)
                    throw new AccountStateShadowRpcException(-32009, "revision_conflict");
                updated = current with { Nickname = mutation.Nickname, Revision = NextRevision() };
                _nicknames[mutation.AccountId] = updated;
                var source = _source[mutation.AccountId];
                var updatedSource = source with { SafeFallbackDisplayName = mutation.Nickname is null ? mutation.SafeFallbackDisplayName : "Account" };
                _source[mutation.AccountId] = updatedSource;
                account = Project(updatedSource, updated);
            }

            if (fault == UpdateFault.TransportLostAfterCommitAndDisconnect)
                IsAvailable = false;
            if (fault == UpdateFault.TransportLostThenChangedByAnotherClient)
                SetExternalNickname(Guid.Parse(mutation.AccountId), "Concurrent winner");
            if (fault is UpdateFault.TransportLostAfterCommit or UpdateFault.TransportLostAfterCommitAndDisconnect or UpdateFault.TransportLostThenChangedByAnotherClient)
                throw new AccountStateShadowRpcOutcomeUnknownException();

            return await Task.FromResult(new NicknameMutationResult { NicknameState = updated, Account = account });
        }

        public async Task<SourceReconcileResult> ReconcileSourceAsync(
            AccountSourceCollection collection,
            long expectedAuthorityEpoch,
            string? migrationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailable();
            TaskCompletionSource? entered;
            TaskCompletionSource? release;
            lock (_sync)
            {
                entered = _reconcileBlockArmed ? _reconcileEntered : null;
                release = _reconcileBlockArmed ? _reconcileRelease : null;
                _reconcileBlockArmed = false;
            }
            if (entered is not null && release is not null)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    if (ReferenceEquals(_reconcileRelease, release))
                    {
                        _reconcileEntered = null;
                        _reconcileRelease = null;
                    }
                }
            }
            EnsureAvailable();
            if (SourceReconcileFailuresRemaining > 0)
            {
                SourceReconcileFailuresRemaining--;
                throw new IOException("Synthetic source reconcile interruption.");
            }

            lock (_sync)
            {
                var next = collection.Accounts.ToDictionary(item => item.AccountId, StringComparer.Ordinal);
                foreach (var (id, state) in _nicknames.ToArray())
                {
                    if (!next.ContainsKey(id))
                        _nicknames.Remove(id);
                }
                foreach (var source in collection.Accounts)
                {
                    if (!_nicknames.ContainsKey(source.AccountId))
                        _nicknames.Add(source.AccountId, new AccountNicknameState { AccountId = source.AccountId, Nickname = null, Revision = NextRevision() });
                }
                foreach (var (id, source) in next)
                {
                    if (!_source.TryGetValue(id, out var old) || old != source)
                    {
                        if (_nicknames.TryGetValue(id, out var nickname))
                            _nicknames[id] = nickname with { Revision = NextRevision() };
                    }
                }
                _source.Clear();
                foreach (var pair in next)
                    _source.Add(pair.Key, pair.Value);
            }
            return new SourceReconcileResult { Unchanged = collection.Accounts.Count };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private AccountDescriptor Project(AccountSourceDescriptor source)
        {
            return Project(source, _nicknames[source.AccountId]);
        }

        private static AccountDescriptor Project(AccountSourceDescriptor source, AccountNicknameState nickname) => new(
            1,
            source.AccountId,
            nickname.Nickname ?? source.SafeFallbackDisplayName,
            source.Health,
            source.IsActive,
            source.SortOrder,
            source.CreatedAt,
            source.PlanType);

        private string NextRevision() => $"revision-{Interlocked.Increment(ref _revision)}";

        private void EnsureAvailable()
        {
            if (!IsAvailable)
                throw new IOException("Synthetic daemon unavailable.");
        }
    }

    private sealed class MemoryProfileStore : IProfileStore
    {
        private List<ProfileMetadata> _profiles = [];
        public bool FailNextSave { get; set; }
        public List<ProfileMetadata> LoadAll() => _profiles.Select(profile => profile.CreateSnapshot()).ToList();

        public void SaveAll(IEnumerable<ProfileMetadata> profiles, ProfileSaveIntent intent = ProfileSaveIntent.NormalUpdate)
        {
            _ = intent;
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("Synthetic profile cache persistence failure.");
            }
            _profiles = profiles.Select(profile => profile.CreateSnapshot()).ToList();
        }
    }

    private sealed class SyntheticVault : IVaultService
    {
        public IProfileOperationCoordinator Coordinator => null!;
        public string BlobPath(Guid profileId) => $"synthetic:{profileId:D}";
        public bool Exists(Guid profileId) => false;
        public string SaveBlob(Guid profileId, byte[] authJson) => "synthetic-fingerprint";
        public (bool Success, string? CurrentFingerprint) SaveBlobIfUnchanged(Guid profileId, string expectedOriginalFingerprint, byte[] newAuthJson) => (false, null);
        public Task<(bool Success, string? CurrentFingerprint)> SaveBlobIfUnchangedAsync(Guid profileId, string expectedOriginalFingerprint, byte[] newAuthJson, CancellationToken cancellationToken = default) => Task.FromResult((false, (string?)null));
        public byte[] LoadBlob(Guid profileId) => throw new FileNotFoundException();
        public void DeleteBlob(Guid profileId) { }
        public IReadOnlyList<Guid> EnumerateBlobs() => [];
        public void SaveEncryptedFile(string path, byte[] plaintext) => throw new NotSupportedException();
        public byte[] LoadEncryptedFile(string path) => throw new NotSupportedException();
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
    }

    private sealed class NoopAuditLog : IAuditLog
    {
        public void Record(string action, string outcome, string? detail = null) { }
    }

    public enum UpdateFault
    {
        None,
        ExplicitFailureBeforeCommit,
        TransportLostBeforeCommit,
        TransportLostAfterCommit,
        TransportLostThenChangedByAnotherClient,
        TransportLostAfterCommitAndDisconnect
    }
}
