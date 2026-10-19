using System.Text;
using System.Text.Json;

namespace CodexSwitcher.Core.Tests;

public sealed class AccountServiceBoundaryTests
{
    [Fact]
    public void AccountRegistry_LoadsFindsAndPersistsMetadataOnlyOperations()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var store = new ProfileStore(fs, temp.Combine("profiles.json"));
        var clock = new FakeClock();
        var audit = new FakeAudit();
        var first = new ProfileMetadata { Id = Guid.NewGuid(), AccountSub = "first", Nickname = "First", SortOrder = 0 };
        var second = new ProfileMetadata { Id = Guid.NewGuid(), AccountSub = "second", Nickname = "Second", SortOrder = 1 };
        store.SaveAll([first, second]);

        var registry = new AccountRegistry(store, clock, audit);
        registry.Load();

        Assert.Equal(first.Id, registry.FindByAccountSub("first")?.Id);
        registry.Rename(first.Id, " Renamed ");
        registry.Reorder([second.Id, first.Id]);

        var reloaded = new AccountRegistry(store, clock, audit);
        reloaded.Load();
        Assert.Equal("Renamed", reloaded.Find(first.Id)?.Nickname);
        Assert.Equal([second.Id, first.Id], reloaded.Profiles.OrderBy(profile => profile.SortOrder).Select(profile => profile.Id));
        Assert.IsNotType<List<ProfileMetadata>>(reloaded.Profiles);
    }

    [Fact]
    public void AccountRegistry_ReorderPersistenceFailureRestoresOwnedSnapshot()
    {
        using var temp = new TempDir();
        var fs = new FaultInjectingFileSystem(new PhysicalFileSystem());
        var path = temp.Combine("profiles.json");
        var store = new ProfileStore(fs, path);
        var clock = new FakeClock();
        var audit = new FakeAudit();
        var first = new ProfileMetadata { Id = Guid.NewGuid(), Nickname = "First", SortOrder = 0 };
        var second = new ProfileMetadata { Id = Guid.NewGuid(), Nickname = "Second", SortOrder = 1 };
        store.SaveAll([first, second]);

        var registry = new AccountRegistry(store, clock, audit);
        registry.Load();
        fs.FailAtomicWriteForPath = path;

        Assert.Throws<IOException>(() => registry.Reorder([second.Id, first.Id]));
        Assert.Equal(first.Id, registry.Profiles.OrderBy(profile => profile.SortOrder).First().Id);

        fs.FailAtomicWriteForPath = null;
        var reloaded = new AccountRegistry(store, clock, audit);
        reloaded.Load();
        Assert.Equal(first.Id, reloaded.Profiles.OrderBy(profile => profile.SortOrder).First().Id);
    }

    [Fact]
    public void AccountCredentialServiceValidatesDeduplicatesAndRejectsStaleRefreshWriteback()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var clock = new FakeClock();
        var audit = new FakeAudit();
        var paths = CodexPaths.ForHome(temp.Combine(".codex"));
        var vault = new VaultService(new DpapiSecretProtector(), fs, temp.Combine("vault"));
        var graph = AccountServiceGraph.Create(
            vault,
            new ProfileStore(fs, temp.Combine("profiles.json")),
            new ReconciliationService(fs, paths),
            fs,
            paths,
            clock,
            audit);
        var initialAuth = Sample.AuthJson(idToken: Sample.Jwt(sub: "credential-sub"), refreshToken: "refresh-one");
        AccountCredentialService.ValidateAuthJson(initialAuth);

        var original = graph.Credentials.AddFromAuthJson(initialAuth, "Original");
        var replacementAuth = Sample.AuthJson(idToken: Sample.Jwt(sub: "credential-sub"), refreshToken: "refresh-two");
        var duplicate = graph.Credentials.AddFromAuthJson(replacementAuth, "Updated");

        Assert.Equal(original.Id, duplicate.Id);
        Assert.Single(graph.Registry.Profiles);
        Assert.Equal("Updated", duplicate.Nickname);
        Assert.True(vault.Exists(original.Id));

        var expectedFingerprint = duplicate.BlobFingerprint!;
        var refreshedAuth = Sample.AuthJson(
            idToken: Sample.Jwt(sub: "credential-sub"),
            refreshToken: "refresh-three",
            lastRefresh: "2026-06-26T10:00:00Z");
        Assert.True(graph.Credentials.PersistIsolatedLaunchRefreshUnderLock(original.Id, expectedFingerprint, refreshedAuth));
        Assert.False(graph.Credentials.PersistIsolatedLaunchRefreshUnderLock(original.Id, expectedFingerprint, replacementAuth));
        Assert.Equal(Fingerprint.Compute(refreshedAuth), graph.Registry.Find(original.Id)?.BlobFingerprint);
        Assert.Throws<InvalidDataException>(() => AccountCredentialService.ValidateAuthJson("{}"u8.ToArray()));
    }

    [Fact]
    public void SubscriptionMetadataServiceEnrichesMatchingIdentityAndIgnoresMismatchedIdentity()
    {
        var authJson = AuthWithSubscription("account-sub", "Detected Name");
        var now = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        var matching = new ProfileMetadata
        {
            AccountSub = "account-sub",
            SubscriptionTracking = new SubscriptionTracking(null, new DateOnly(2026, 10, 5), SubscriptionTrackingMode.Renewal),
        };
        var mismatched = new ProfileMetadata { AccountSub = "different-sub", DetectedDisplayName = "Keep Me" };

        Assert.True(SubscriptionMetadataService.EnrichMissing(matching, authJson, now));
        Assert.Equal("Detected Name", matching.DetectedDisplayName);
        Assert.NotNull(matching.DetectedSubscription);
        Assert.Equal(new DateOnly(2026, 10, 5), matching.SubscriptionTracking!.NextRenewalOrExpiryOn);
        Assert.False(SubscriptionMetadataService.EnrichMissing(mismatched, authJson, now));
        Assert.Equal("Keep Me", mismatched.DetectedDisplayName);
        Assert.Null(mismatched.DetectedSubscription);
    }

    [Fact]
    public void CodexAuthReconcilerDetectsAndAdoptsUnmanagedActiveCredential()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var clock = new FakeClock();
        var audit = new FakeAudit();
        var paths = CodexPaths.ForHome(temp.Combine(".codex"));
        var authJson = Sample.AuthJson(idToken: Sample.Jwt(sub: "unmanaged-sub"));
        fs.WriteAllBytesAtomic(paths.ActiveAuthPath, authJson);
        var vault = new VaultService(new DpapiSecretProtector(), fs, temp.Combine("vault"));
        var graph = AccountServiceGraph.Create(
            vault,
            new ProfileStore(fs, temp.Combine("profiles.json")),
            new ReconciliationService(fs, paths),
            fs,
            paths,
            clock,
            audit);

        Assert.True(graph.AuthReconciler.HasUnmanagedActiveAccount());
        var adopted = graph.AuthReconciler.AdoptActiveAccount("Adopted");

        Assert.NotNull(adopted);
        Assert.True(adopted.IsActive);
        Assert.Equal("unmanaged-sub", adopted.AccountSub);
        Assert.Equal(authJson, vault.LoadBlob(adopted.Id));
        Assert.False(graph.AuthReconciler.HasUnmanagedActiveAccount());
    }

    [Fact]
    public void CodexAuthReconcilerWritesBackSameAccountCredentialDrift()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var clock = new FakeClock();
        var audit = new FakeAudit();
        var paths = CodexPaths.ForHome(temp.Combine(".codex"));
        var initialAuth = Sample.AuthJson(idToken: Sample.Jwt(sub: "drift-sub"), refreshToken: "before-refresh");
        var driftedAuth = Sample.AuthJson(
            idToken: Sample.Jwt(sub: "drift-sub"),
            refreshToken: "after-refresh",
            lastRefresh: "2026-06-26T10:00:00Z");
        fs.WriteAllBytesAtomic(paths.ActiveAuthPath, initialAuth);
        var vault = new VaultService(new DpapiSecretProtector(), fs, temp.Combine("vault"));
        var graph = AccountServiceGraph.Create(
            vault,
            new ProfileStore(fs, temp.Combine("profiles.json")),
            new ReconciliationService(fs, paths),
            fs,
            paths,
            clock,
            audit);
        var profile = graph.Credentials.AddFromAuthJson(initialAuth, "Drift");

        Assert.Equal(ActiveMatch.Exact, graph.AuthReconciler.Reconcile().Match);
        fs.WriteAllBytesAtomic(paths.ActiveAuthPath, driftedAuth);

        Assert.Equal(ActiveMatch.SameAccountDrifted, graph.AuthReconciler.Reconcile().Match);
        Assert.Equal(driftedAuth, vault.LoadBlob(profile.Id));
        Assert.Equal(Fingerprint.Compute(driftedAuth), graph.Registry.Find(profile.Id)?.BlobFingerprint);
    }

    [Fact]
    public void AccountTransferServiceRoundTripsAndValidatesBeforeMutatingDestination()
    {
        using var sourceTemp = new TempDir();
        using var destinationTemp = new TempDir();
        var source = CreateGraph(sourceTemp);
        var destination = CreateGraph(destinationTemp);
        var first = source.Credentials.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "transfer-one")), "First");
        var second = source.Credentials.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "transfer-two")), "Second");
        source.Registry.Reorder([second.Id, first.Id]);

        Assert.Throws<InvalidDataException>(() => destination.Transfer.Import("{}"));
        var invalidBundle = JsonSerializer.Serialize(new AccountTransferDocument
        {
            Accounts =
            [
                new TransferredAccount { Nickname = "Valid first entry", AuthJsonBase64 = Convert.ToBase64String(Sample.AuthJson(idToken: Sample.Jwt(sub: "not-imported"))) },
                new TransferredAccount { Nickname = "Invalid second entry", AuthJsonBase64 = Convert.ToBase64String("{}"u8.ToArray()) },
            ],
        });
        Assert.Throws<InvalidDataException>(() => destination.Transfer.Import(invalidBundle));
        Assert.Empty(destination.Registry.Profiles);
        Assert.Equal(2, destination.Transfer.Import(source.Transfer.ExportAll()));
        Assert.Equal(
            ["Second", "First"],
            destination.Registry.Profiles.OrderBy(profile => profile.SortOrder).Select(profile => profile.Nickname));
    }

    private static AccountServiceGraph CreateGraph(TempDir temp)
    {
        var fs = new PhysicalFileSystem();
        var paths = CodexPaths.ForHome(temp.Combine(".codex"));
        var clock = new FakeClock();
        var audit = new FakeAudit();
        var vault = new VaultService(new DpapiSecretProtector(), fs, temp.Combine("vault"));
        return AccountServiceGraph.Create(
            vault,
            new ProfileStore(fs, temp.Combine("profiles.json")),
            new ReconciliationService(fs, paths),
            fs,
            paths,
            clock,
            audit);
    }

    private static byte[] AuthWithSubscription(string sub, string displayName)
    {
        static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}"u8.ToArray());
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = sub,
            ["email"] = "account@example.com",
            ["name"] = displayName,
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_subscription_active_start"] = "2026-08-11T12:00:00Z",
                ["chatgpt_subscription_active_until"] = "2026-10-11T12:00:00Z",
                ["chatgpt_plan_type"] = "plus",
            },
        });
        var token = $"{header}.{Base64Url(payload)}.";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            auth_mode = "chatgpt",
            tokens = new { id_token = token, access_token = token, refresh_token = "synthetic" },
        });
    }
}
