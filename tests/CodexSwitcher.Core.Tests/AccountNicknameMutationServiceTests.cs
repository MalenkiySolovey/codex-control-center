using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.AgentState.V1;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Infra.AgentState.Shadow;

namespace CodexSwitcher.Core.Tests;

public sealed class AccountNicknameMutationServiceTests
{
    [Theory]
    [InlineData(null, false, false, false, "off")]
    [InlineData("true", true, true, false, "off")]
    [InlineData("1", false, true, false, "shadow_disabled")]
    [InlineData("1", true, false, false, "read_authority_canary_disabled")]
    [InlineData("1", true, true, true, "enabled")]
    public void WriteCanaryRequiresExplicitOptInAndBothUpstreamCanaries(
        string? optIn,
        bool shadowEnabled,
        bool readEnabled,
        bool expectedEnabled,
        string expectedCategory)
    {
        var settings = AccountNicknameWriteCanarySettings.Parse(optIn, shadowEnabled, readEnabled);

        Assert.Equal(expectedEnabled, settings.Enabled);
        Assert.Equal(expectedCategory, settings.StartupCategory);
    }

    [Fact]
    public async Task DisabledCanaryKeepsRenameLocalAndDoesNotConnectToDaemon()
    {
        await using var fixture = await Fixture.CreateAsync(writeCanaryOptIn: "0");
        var generation = fixture.Registry.CurrentGeneration;
        var connectCount = fixture.Factory.ConnectCount;

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Local rename");

        Assert.Equal(AccountNicknameMutationOutcome.LocalOnly, result.Outcome);
        Assert.Equal("Local rename", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal(generation + 1, fixture.Registry.CurrentGeneration);
        Assert.Equal(connectCount, fixture.Factory.ConnectCount);
        Assert.Equal(0, fixture.Client.UpdateMetadataCount);
    }

    [Fact]
    public async Task EligibleRenameUsesDaemonCasThenMirrorsLocalAndReconciles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var generation = fixture.Registry.CurrentGeneration;

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Synthetic renamed");

        Assert.Equal(AccountNicknameMutationOutcome.DaemonAndLocal, result.Outcome);
        Assert.Equal("Synthetic renamed", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal("synthetic-account@example.invalid", fixture.Registry.Find(fixture.Profile.Id)!.AccountEmail);
        Assert.Equal("synthetic-subject", fixture.Registry.Find(fixture.Profile.Id)!.AccountSub);
        Assert.True(fixture.Registry.Find(fixture.Profile.Id)!.IsActive);
        Assert.True(fixture.Registry.CurrentGeneration > generation);
        Assert.Equal(1, fixture.Client.UpdateMetadataCount);
        Assert.Equal("Synthetic renamed", (await fixture.Client.GetAccountAsync(fixture.Profile.Id.ToString("D"), CancellationToken.None)).Account.DisplayName);
        await WaitUntilAsync(() => fixture.Coordinator.LastStatus.Category == "synchronized" &&
            fixture.Coordinator.LastStatus.LastMatchedGeneration == fixture.Registry.CurrentGeneration);

        var states = fixture.Diagnostics.Transitions.Select(transition => transition.State).ToArray();
        Assert.Contains("Ready", states);
        Assert.Contains("UpdatingDaemon", states);
        Assert.Contains("MirroringLocal", states);
        Assert.Contains("WaitingForReconcile", states);
        Assert.Contains("Converged", states);
        Assert.Equal("Synthetic renamed", fixture.ReadProvider.Read().Accounts.Single().DisplayName);
    }

    [Fact]
    public async Task IdenticalNicknameIsNoOpAndDoesNotAdvanceGenerationOrWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var generation = fixture.Registry.CurrentGeneration;

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "  Synthetic original  ");

        Assert.Equal(AccountNicknameMutationOutcome.NoOp, result.Outcome);
        Assert.Equal(generation, fixture.Registry.CurrentGeneration);
        Assert.Equal(0, fixture.Client.UpdateMetadataCount);
    }

    [Fact]
    public async Task UnsupportedDaemonCapabilityFallsBackToExistingLocalRename()
    {
        await using var fixture = await Fixture.CreateAsync(supportsNicknameMutation: false);

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Local fallback");

        Assert.Equal(AccountNicknameMutationOutcome.LocalOnly, result.Outcome);
        Assert.Equal("Local fallback", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal(0, fixture.Client.UpdateMetadataCount);
    }

    [Theory]
    [InlineData("method_not_found", -32601, "method_not_found")]
    [InlineData("params_invalid", -32602, "validation_failed")]
    [InlineData("account_missing", -32004, "not_found")]
    [InlineData("database_unhealthy", -32000, "database_unhealthy")]
    [InlineData("protocol_incompatible", 0, "malformed_or_protocol_incompatible")]
    [InlineData("pipe_closed", 0, "pipe_closed")]
    [InlineData("timeout", 0, "timeout")]
    public async Task PreflightRpcFailuresRemainLocalAndKeepDistinctSanitizedCategories(
        string failure,
        int code,
        string expectedCategory)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.FailureOnGet = failure switch
        {
            "method_not_found" => new AccountStateShadowRpcException(code, "method_not_found"),
            "params_invalid" => new AccountStateShadowRpcException(code, "validation_failed"),
            "account_missing" => new AccountStateShadowRpcException(code, "not_found"),
            "database_unhealthy" => new AccountStateShadowRpcException(code, "database_unhealthy"),
            "protocol_incompatible" => new AccountStateShadowCompatibilityException(),
            "pipe_closed" => new EndOfStreamException(),
            "timeout" => new AccountStateShadowRpcTimeoutException(),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Local after read failure");

        Assert.Equal(AccountNicknameMutationOutcome.LocalOnly, result.Outcome);
        Assert.Equal(expectedCategory, result.Category);
        Assert.Equal("Local after read failure", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal(0, fixture.Client.UpdateMetadataCount);
    }

    [Fact]
    public async Task RetryableDatabaseBusyIsMappedSeparatelyAndDoesNotRetryTheMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.FailureOnUpdate = new AccountStateShadowRpcException(-32000, "storage_failure", retryable: true);

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Local after busy");

        Assert.Equal(AccountNicknameMutationOutcome.LocalOnly, result.Outcome);
        Assert.Equal("database_busy", result.Category);
        Assert.Equal("Local after busy", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal(1, fixture.Client.UpdateMetadataCount);
    }

    [Fact]
    public async Task MalformedUpdateResponseIsVerifiedByReadbackBeforeLocalMirror()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.ReturnMismatchedUpdateResponse = true;

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Readback verified");

        Assert.Equal(AccountNicknameMutationOutcome.DaemonAndLocal, result.Outcome);
        Assert.Equal("Readback verified", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal(1, fixture.Client.UpdateMetadataCount);
    }

    [Fact]
    public async Task StaleCasConflictWithDifferentRemoteNameDoesNotOverwriteOrRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.BeforeNextUpdate = client => client.SetExternalName("Other writer", "external-revision");

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "User request");

        Assert.Equal(AccountNicknameMutationOutcome.Conflict, result.Outcome);
        Assert.Equal("Synthetic original", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal(1, fixture.Client.UpdateMetadataCount);
        await WaitUntilAsync(() => fixture.Coordinator.LastStatus.Category == "synchronized");
        Assert.Equal("Synthetic original", (await fixture.Client.GetAccountAsync(fixture.Profile.Id.ToString("D"), CancellationToken.None)).Account.DisplayName);
    }

    [Fact]
    public async Task StaleCasConflictAlreadyAtRequestedNameIsRecognizedAndMirrored()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.BeforeNextUpdate = client => client.SetExternalName("User request", "external-revision");

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "User request");

        Assert.Equal(AccountNicknameMutationOutcome.DaemonAndLocal, result.Outcome);
        Assert.Equal("User request", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal(1, fixture.Client.UpdateMetadataCount);
    }

    [Fact]
    public async Task LocalPersistenceFailureCompensatesDaemonOnceAndPreservesGenerationAndPreimage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var generation = fixture.Registry.CurrentGeneration;
        fixture.Store.OnNextSave = () =>
        {
            Assert.Equal(AccountReadSource.Local, fixture.ReadProvider.Read().Source);
            Assert.Equal(AccountReadCanaryState.LocalFallbackDirty, fixture.ReadProvider.Read().CanaryState);
        };
        fixture.Store.FailNextSave = true;

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Persist failure");

        Assert.Equal(AccountNicknameMutationOutcome.FailedCompensated, result.Outcome);
        Assert.Equal(generation, fixture.Registry.CurrentGeneration);
        Assert.Equal("Synthetic original", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal("synthetic-account@example.invalid", fixture.Registry.Find(fixture.Profile.Id)!.AccountEmail);
        Assert.Equal("synthetic-subject", fixture.Registry.Find(fixture.Profile.Id)!.AccountSub);
        Assert.Equal("Synthetic original", (await fixture.Client.GetAccountAsync(fixture.Profile.Id.ToString("D"), CancellationToken.None)).Account.DisplayName);
        Assert.Equal(2, fixture.Client.UpdateMetadataCount);
    }

    [Fact]
    public async Task FailedCompensationKeepsLocalAuthorityAndRequestsReconciliationWithoutSuccess()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.FailOnUpdateCall = 2;
        fixture.Store.FailNextSave = true;

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Persist failure");

        Assert.Equal(AccountNicknameMutationOutcome.FailedDiverged, result.Outcome);
        Assert.Equal("Synthetic original", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.Equal(2, fixture.Client.UpdateMetadataCount);
        await WaitUntilAsync(() => fixture.Coordinator.LastStatus.Category == "synchronized");
        Assert.Equal("Synthetic original", (await fixture.Client.GetAccountAsync(fixture.Profile.Id.ToString("D"), CancellationToken.None)).Account.DisplayName);
    }

    [Fact]
    public async Task ConcurrentLocalGenerationChangeIsCompensatedToLatestLocalNickname()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Client.AfterNextUpdate = () => fixture.Registry.MarkUsed(fixture.Profile.Id);

        var result = await fixture.Service.RenameAsync(fixture.Profile.Id, "Concurrent request");

        Assert.Equal(AccountNicknameMutationOutcome.FailedCompensated, result.Outcome);
        Assert.Equal("Synthetic original", fixture.Registry.Find(fixture.Profile.Id)!.Nickname);
        Assert.True(fixture.Registry.Find(fixture.Profile.Id)!.MarkedUsedAt.HasValue);
        Assert.Equal("Synthetic original", (await fixture.Client.GetAccountAsync(fixture.Profile.Id.ToString("D"), CancellationToken.None)).Account.DisplayName);
        Assert.Equal(2, fixture.Client.UpdateMetadataCount);
    }

    [Fact]
    public async Task DiagnosticsContainOnlySanitizedStagesNotIdentityNamesOrRevision()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Service.RenameAsync(fixture.Profile.Id, "private synthetic nickname");

        var trace = System.Text.Json.JsonSerializer.Serialize(fixture.Diagnostics.Transitions);
        Assert.Contains("UpdatingDaemon", trace, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Profile.Id.ToString("D"), trace, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic original", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("private synthetic nickname", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("revision-", trace, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(predicate(), "The expected canary state was not observed before timeout.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            SwitchableProfileStore store,
            AccountRegistry registry,
            ProfileMetadata profile,
            FakeClientFactory factory,
            FakeClient client,
            AccountStateShadowCoordinator coordinator,
            AccountReadSnapshotProvider readProvider,
            AccountNicknameMutationDiagnostics diagnostics,
            IAccountNicknameMutationService service)
        {
            Store = store;
            Registry = registry;
            Profile = profile;
            Factory = factory;
            Client = client;
            Coordinator = coordinator;
            ReadProvider = readProvider;
            Diagnostics = diagnostics;
            Service = service;
        }

        public SwitchableProfileStore Store { get; }
        public AccountRegistry Registry { get; }
        public ProfileMetadata Profile { get; }
        public FakeClientFactory Factory { get; }
        public FakeClient Client { get; }
        public AccountStateShadowCoordinator Coordinator { get; }
        public AccountReadSnapshotProvider ReadProvider { get; }
        public AccountNicknameMutationDiagnostics Diagnostics { get; }
        public IAccountNicknameMutationService Service { get; }

        public static async Task<Fixture> CreateAsync(
            string? writeCanaryOptIn = "1",
            bool supportsNicknameMutation = true)
        {
            var store = new SwitchableProfileStore();
            var registry = new AccountRegistry(store, new FakeClock(), new FakeAudit());
            registry.Load();
            var profile = CreateProfile();
            registry.Add(profile);

            var shadowSettings = AccountStateShadowSettings.Parse("1", "C:\\synthetic\\agent-state-daemon.exe");
            var readSettings = AccountReadAuthorityCanarySettings.Parse("1", shadowSettings.Enabled);
            var writeSettings = AccountNicknameWriteCanarySettings.Parse(writeCanaryOptIn, shadowSettings.Enabled, readSettings.Enabled);
            var client = new FakeClient(supportsNicknameMutation);
            var factory = new FakeClientFactory(client);
            var diagnostics = new AccountNicknameMutationDiagnostics();
            var coordinator = new AccountStateShadowCoordinator(registry, shadowSettings, factory, readSettings, diagnostics);
            var readProvider = new AccountReadSnapshotProvider(registry, coordinator, readSettings);
            var service = new AccountNicknameMutationService(registry, writeSettings, coordinator, factory, diagnostics);
            if (writeSettings.Enabled)
            {
                await coordinator.StartAsync(CancellationToken.None);
                await WaitUntilAsync(() => coordinator.LastStatus.Category == "synchronized" &&
                    coordinator.LastStatus.LastMatchedGeneration == registry.CurrentGeneration);
            }
            return new Fixture(store, registry, profile, factory, client, coordinator, readProvider, diagnostics, service);
        }

        public async ValueTask DisposeAsync() => await Coordinator.StopAsync(CancellationToken.None);
    }

    private static ProfileMetadata CreateProfile() => new()
    {
        Id = Guid.NewGuid(),
        Nickname = "Synthetic original",
        AccountEmail = "synthetic-account@example.invalid",
        AccountSub = "synthetic-subject",
        DetectedDisplayName = "Synthetic base",
        CreatedAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
        HealthStatus = HealthStatus.Valid,
        IsActive = true,
        SortOrder = 0
    };

    private sealed class SwitchableProfileStore : IProfileStore
    {
        private List<ProfileMetadata> _profiles = [];

        public bool FailNextSave { get; set; }
        public Action? OnNextSave { get; set; }

        public List<ProfileMetadata> LoadAll() => _profiles.ToList();

        public void SaveAll(IEnumerable<ProfileMetadata> profiles, ProfileSaveIntent intent = ProfileSaveIntent.NormalUpdate)
        {
            _ = intent;
            var onSave = OnNextSave;
            OnNextSave = null;
            onSave?.Invoke();
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("synthetic persistence failure");
            }

            _profiles = profiles.ToList();
        }
    }

    private sealed class FakeClientFactory(FakeClient client) : IAccountStateShadowClientFactory
    {
        public int ConnectCount { get; private set; }
        public bool IsLastConnectionHealthy { get; private set; }

        public Task<IAccountStateShadowClient> ConnectCompatibleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            IsLastConnectionHealthy = true;
            return Task.FromResult<IAccountStateShadowClient>(client);
        }

        public Task StopOwnedProcessAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsLastConnectionHealthy = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClient(bool supportsNicknameMutation) : IAccountStateShadowClient
    {
        private readonly object _sync = new();
        private Dictionary<string, RevisionedAccountDescriptor> _accounts = new(StringComparer.Ordinal);
        private int _revisionCounter;
        private int _updateMetadataCount;

        public bool SupportsNicknameMutation { get; } = supportsNicknameMutation;
        public int UpdateMetadataCount => Volatile.Read(ref _updateMetadataCount);
        public int? FailOnUpdateCall { get; set; }
        public Exception? FailureOnGet { get; set; }
        public Exception? FailureOnUpdate { get; set; }
        public bool ReturnMismatchedUpdateResponse { get; set; }
        public Action<FakeClient>? BeforeNextUpdate { get; set; }
        public Action? AfterNextUpdate { get; set; }

        public Task<AccountStateShadowReconcileCounts> ReconcileAsync(AccountCollection collection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                var oldIds = _accounts.Keys.ToHashSet(StringComparer.Ordinal);
                var updatedCount = 0;
                var next = new Dictionary<string, RevisionedAccountDescriptor>(StringComparer.Ordinal);
                foreach (var descriptor in collection.Accounts)
                {
                    if (_accounts.TryGetValue(descriptor.AccountId, out var previous) &&
                        previous.Account == descriptor)
                    {
                        next.Add(descriptor.AccountId, previous);
                    }
                    else
                    {
                        if (previous is not null)
                            updatedCount++;
                        next.Add(descriptor.AccountId, new RevisionedAccountDescriptor(descriptor, NextRevision()));
                    }
                    oldIds.Remove(descriptor.AccountId);
                }

                var inserted = next.Keys.Count(accountId => !_accounts.ContainsKey(accountId));
                _accounts = next;
                return Task.FromResult(new AccountStateShadowReconcileCounts(inserted, updatedCount, oldIds.Count, 0));
            }
        }

        public Task<IReadOnlyList<AccountDescriptor>> ListAccountsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
                return Task.FromResult<IReadOnlyList<AccountDescriptor>>(_accounts.Values.Select(account => account.Account).ToArray());
        }

        public Task<RevisionedAccountDescriptor> GetAccountAsync(string accountId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailureOnGet is { } failure)
                throw failure;
            lock (_sync)
            {
                if (!_accounts.TryGetValue(accountId, out var account))
                    throw new AccountStateShadowRpcException(-32004, "not_found");
                return Task.FromResult(account);
            }
        }

        public Task<RevisionedAccountDescriptor> UpdateMetadataAsync(AccountMetadataUpdate update, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updateNumber = Interlocked.Increment(ref _updateMetadataCount);
            var beforeUpdate = BeforeNextUpdate;
            BeforeNextUpdate = null;
            beforeUpdate?.Invoke(this);

            if (FailureOnUpdate is { } failure)
                throw failure;

            lock (_sync)
            {
                if (FailOnUpdateCall == updateNumber)
                    throw new AccountStateShadowRpcException(-32000, "storage_failure");
                if (!_accounts.TryGetValue(update.AccountId, out var current))
                    throw new AccountStateShadowRpcException(-32004, "not_found");
                if (!string.Equals(update.ExpectedRevision, current.Revision, StringComparison.Ordinal))
                    throw new AccountStateShadowRpcException(-32009, "revision_conflict");

                var changed = new RevisionedAccountDescriptor(
                    current.Account with { DisplayName = string.IsNullOrEmpty(update.Nickname) ? "Synthetic base" : update.Nickname },
                    NextRevision());
                _accounts[update.AccountId] = changed;
                var afterUpdate = AfterNextUpdate;
                AfterNextUpdate = null;
                afterUpdate?.Invoke();
                return Task.FromResult(ReturnMismatchedUpdateResponse
                    ? changed with { Account = changed.Account with { AccountId = "synthetic-wrong-account" } }
                    : changed);
            }
        }

        public void SetExternalName(string nickname, string revision)
        {
            lock (_sync)
            {
                var accountId = Assert.Single(_accounts.Keys);
                var current = _accounts[accountId];
                _accounts[accountId] = new RevisionedAccountDescriptor(current.Account with { DisplayName = nickname }, revision);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private string NextRevision() => $"revision-{Interlocked.Increment(ref _revisionCounter)}";
    }
}
