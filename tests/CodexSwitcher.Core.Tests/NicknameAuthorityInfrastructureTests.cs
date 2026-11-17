using System.Text.Json;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.AgentState.V1;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.AgentState.Shadow;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;

namespace CodexSwitcher.Core.Tests;

public sealed class NicknameAuthorityInfrastructureTests
{
    [Fact]
    public void CanaryIsOptInAndRejectsNetworkOrUnqualifiedDaemonPaths()
    {
        var disabled = NicknameAuthorityCanarySettings.Parse(null, "C:\\synthetic\\agent-state-daemon.exe");
        var enabled = NicknameAuthorityCanarySettings.Parse("1", "C:\\synthetic\\agent-state-daemon.exe");
        var network = NicknameAuthorityCanarySettings.Parse("1", "\\\\server\\share\\agent-state-daemon.exe");
        var relative = NicknameAuthorityCanarySettings.Parse("1", "agent-state-daemon.exe");

        Assert.False(disabled.Enabled);
        Assert.Equal("off", disabled.StartupCategory);
        Assert.True(enabled.Enabled);
        Assert.Equal("enabled", enabled.StartupCategory);
        Assert.False(network.CanStartDaemon);
        Assert.Equal("configuration_invalid", network.StartupCategory);
        Assert.False(relative.CanStartDaemon);
        Assert.Equal("configuration_invalid", relative.StartupCategory);
    }

    [Fact]
    public void MigrationJournalPersistsOnlyOpaqueIdentifiersAndLifecycleMetadata()
    {
        using var temp = new TemporaryDirectory();
        var paths = new AppPaths(
            root: Path.Combine(temp.Path, "switchboard"),
            codexHome: Path.Combine(temp.Path, "codex-home"),
            legacyRoot: Path.Combine(temp.Path, "legacy"));
        paths.EnsureDirectories();
        var journal = new NicknameAuthorityMigrationJournal(new PhysicalFileSystem(), paths);
        var accountId = Guid.NewGuid();
        journal.Save(new NicknameAuthorityJournal
        {
            AuthorityState = NicknameAuthorityStates.Preparing,
            AuthorityEpoch = 2,
            MigrationId = "synthetic-migration-id",
            SourceGeneration = 7,
            Rows = [new NicknameAuthorityMigrationRow
            {
                AccountId = accountId.ToString("D"),
                DaemonRevision = "synthetic-revision",
                Phase = "pending"
            }]
        });

        var text = File.ReadAllText(Path.Combine(paths.Root, "agent-state-shadow-v1", "nickname-authority-migration-v1.json"));
        using var document = JsonDocument.Parse(text);
        var restored = journal.Read();

        Assert.Equal(NicknameAuthorityStates.Preparing, restored!.AuthorityState);
        Assert.Equal(accountId.ToString("D"), restored.Rows[0].AccountId);
        Assert.DoesNotContain("nickname", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Synthetic secret marker", text, StringComparison.Ordinal);
        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public void NicknameMirrorWritesRequireTheExactCompleteProfileSet()
    {
        using var temp = new TemporaryDirectory();
        var store = new ProfileStore(new PhysicalFileSystem(), Path.Combine(temp.Path, "profiles.json"));
        var registry = new AccountRegistry(store, new TestClock(), new NoopAuditLog());
        registry.Load();
        var first = new ProfileMetadata { Id = Guid.NewGuid(), Nickname = "First old" };
        var second = new ProfileMetadata { Id = Guid.NewGuid(), Nickname = "Second old" };
        registry.Add(first);
        registry.Add(second);
        var generation = registry.CurrentGeneration;

        Assert.False(registry.UpdateNicknameMirrorsIfGeneration(
            new Dictionary<Guid, string?> { [first.Id] = "First new" }, generation));
        Assert.False(registry.VerifyPersistedNicknameMirrors(new Dictionary<Guid, string?> { [first.Id] = "First old" }));
        Assert.Equal("First old", registry.Find(first.Id)!.Nickname);
        Assert.Equal("Second old", registry.Find(second.Id)!.Nickname);

        var complete = new Dictionary<Guid, string?>
        {
            [first.Id] = "First new",
            [second.Id] = null
        };
        Assert.True(registry.UpdateNicknameMirrorsIfGeneration(complete, generation));
        Assert.True(registry.VerifyPersistedNicknameMirrors(complete));
        Assert.Equal("First new", registry.Find(first.Id)!.Nickname);
        Assert.Equal(string.Empty, registry.Find(second.Id)!.Nickname);
    }

    [Fact]
    public async Task MigrationLeaseIsExclusiveAndCancellationDoesNotLeakOwnership()
    {
        using var temp = new TemporaryDirectory();
        var paths = new AppPaths(
            root: Path.Combine(temp.Path, "switchboard"),
            codexHome: Path.Combine(temp.Path, "codex-home"),
            legacyRoot: Path.Combine(temp.Path, "legacy"));
        paths.EnsureDirectories();

        await using (var held = await ProfileMigrationLease.AcquireAsync(paths, CancellationToken.None))
        {
            Assert.False(held.WasAbandoned);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ProfileMigrationLease.AcquireAsync(paths, cancellation.Token));
        }

        await using var recovered = await ProfileMigrationLease.AcquireAsync(paths, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(recovered.WasAbandoned);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-switchboard-nickname-authority-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

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
}
