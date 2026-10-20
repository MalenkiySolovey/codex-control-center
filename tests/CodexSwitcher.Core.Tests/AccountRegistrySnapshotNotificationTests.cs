using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Tests.TestSupport;

namespace CodexSwitcher.Core.Tests;

public sealed class AccountRegistrySnapshotNotificationTests
{
    [Fact]
    public void SnapshotChangedIsRaisedOnlyAfterSuccessfulPersistence()
    {
        var store = new InMemoryProfileStore();
        var registry = new AccountRegistry(store, new FakeClock(), new FakeAudit());
        var notifications = 0;
        registry.SnapshotChanged += () =>
        {
            notifications++;
            Assert.Equal(1, store.SaveCount);
            Assert.Equal(1, store.LastSavedCount);
            Assert.Single(registry.Snapshot());
        };

        registry.Add(CreateProfile("Synthetic display"));

        Assert.Equal(1, notifications);
        Assert.Equal(1, registry.CurrentGeneration);
    }

    [Fact]
    public void FailedPersistenceDoesNotPublishAChangedSnapshot()
    {
        var store = new InMemoryProfileStore { FailSave = true };
        var registry = new AccountRegistry(store, new FakeClock(), new FakeAudit());
        var notifications = 0;
        registry.SnapshotChanged += () => notifications++;

        Assert.Throws<IOException>(() => registry.Add(CreateProfile("Synthetic display")));

        Assert.Equal(0, notifications);
        Assert.Empty(registry.Snapshot());
        Assert.Equal(0, registry.CurrentGeneration);
    }

    [Fact]
    public void FailedMutationDoesNotAdvanceAlreadyLoadedGeneration()
    {
        var store = new InMemoryProfileStore();
        var registry = new AccountRegistry(store, new FakeClock(), new FakeAudit());
        registry.Load();
        Assert.Equal(1, registry.CurrentGeneration);

        store.FailSave = true;
        Assert.Throws<IOException>(() => registry.Add(CreateProfile("Not committed")));

        Assert.Equal(1, registry.CurrentGeneration);
        Assert.Empty(registry.Snapshot());
        Assert.Empty(registry.ReadCommittedSnapshot().Profiles);
    }

    [Fact]
    public void LoadPublishesTheInitialSnapshotAfterHydration()
    {
        var store = new InMemoryProfileStore();
        var registry = new AccountRegistry(store, new FakeClock(), new FakeAudit());
        var notifications = 0;
        registry.SnapshotChanged += () =>
        {
            notifications++;
            Assert.True(registry.IsLoaded);
            Assert.Equal(1, registry.CurrentGeneration);
        };

        registry.Load();

        Assert.Equal(1, notifications);
        Assert.Equal(1, registry.ReadCommittedSnapshot().Generation);
    }

    [Fact]
    public void SubscriberExceptionDoesNotCorruptCommittedGenerationOrPersistence()
    {
        var store = new InMemoryProfileStore();
        var registry = new AccountRegistry(store, new FakeClock(), new FakeAudit());
        var notifications = 0;
        registry.SnapshotChanged += () => throw new InvalidOperationException("synthetic subscriber failure");
        registry.SnapshotChanged += () => notifications++;

        var added = registry.Add(CreateProfile("Committed despite observer failure"));

        Assert.Equal(1, registry.CurrentGeneration);
        Assert.Equal(1, notifications);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, store.LastSavedCount);
        Assert.Contains(registry.Snapshot(), profile => profile.Id == added.Id);
    }

    private static ProfileMetadata CreateProfile(string displayName) => new()
    {
        Id = Guid.NewGuid(),
        Nickname = displayName,
        AccountSub = "synthetic-subject",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class InMemoryProfileStore : IProfileStore
    {
        public bool FailSave { get; set; }
        public int SaveCount { get; private set; }
        public int LastSavedCount { get; private set; }

        public List<ProfileMetadata> LoadAll() => [];

        public void SaveAll(IEnumerable<ProfileMetadata> profiles, ProfileSaveIntent intent = ProfileSaveIntent.NormalUpdate)
        {
            SaveCount++;
            if (FailSave)
                throw new IOException("synthetic persistence failure");
            LastSavedCount = profiles.Count();
        }
    }
}
