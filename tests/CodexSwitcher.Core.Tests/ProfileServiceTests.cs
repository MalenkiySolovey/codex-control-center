using CodexSwitcher.Core.Tests.TestSupport;

namespace CodexSwitcher.Core.Tests;

public sealed class ProfileServiceTests
{
    private sealed class Env : IDisposable
    {
        public TempDir Dir { get; } = new();
        public ProfileService Service { get; }
        public AccountRegistry Registry { get; }
        public VaultService Vault { get; }
        public FakeClock Clock { get; } = new();

        public Env()
        {
            var fs = new PhysicalFileSystem();
            Vault = new VaultService(new DpapiSecretProtector(), fs, Dir.Combine("vault"));
            var store = new ProfileStore(fs, Dir.Combine("profiles.json"));
            var paths = CodexPaths.ForHome(Dir.Combine(".codex"));
            var recon = new ReconciliationService(fs, paths);
            var graph = AccountServiceGraph.Create(Vault, store, recon, fs, paths, Clock, new FakeAudit());
            Service = graph.Profiles;
            Registry = graph.Registry;
        }

        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public void AddFromAuthJson_CreatesProfile_WithIdentity()
    {
        using var env = new Env();
        var token = Sample.Jwt(sub: "sub-1", email: "user@x.com");
        var bytes = Sample.AuthJson(idToken: token);

        var p = env.Service.AddFromAuthJson(bytes, nickname: "Trabalho");

        Assert.Equal("Trabalho", p.Nickname);
        Assert.Equal("sub-1", p.AccountSub);
        Assert.Equal("user@x.com", p.AccountEmail);
        Assert.Single(env.Registry.Profiles);
    }

    [Fact]
    public void AddFromAuthJson_PersistsNewOAuthProfileInProtectedVault_AndPreservesExistingProfiles()
    {
        using var env = new Env();
        var firstAuth = Sample.AuthJson(idToken: Sample.Jwt(sub: "saved-account", email: "saved@example.com"));
        var first = env.Service.AddFromAuthJson(firstAuth, "Saved account");
        var firstFingerprint = first.BlobFingerprint;

        var newAuth = Sample.AuthJson(idToken: Sample.Jwt(sub: "oauth-account", email: "oauth@example.com"));
        var added = env.Service.AddFromAuthJson(newAuth, null);

        Assert.NotEqual(first.Id, added.Id);
        Assert.Equal(firstFingerprint, first.BlobFingerprint);
        Assert.Equal("Saved account", first.Nickname);
        Assert.Equal(2, env.Registry.Profiles.Count);
        Assert.Contains(env.Registry.Profiles, profile => profile.Id == first.Id);
        Assert.Contains(env.Registry.Profiles, profile => profile.Id == added.Id);
        Assert.True(env.Vault.Exists(first.Id));
        Assert.True(env.Vault.Exists(added.Id));
        Assert.Equal(newAuth, env.Vault.LoadBlob(added.Id));
        Assert.False(newAuth.SequenceEqual(File.ReadAllBytes(env.Vault.BlobPath(added.Id))));
    }

    [Fact]
    public void AddFromAuthJson_SameSub_DeduplicatesAndUpdates()
    {
        using var env = new Env();
        var token = Sample.Jwt(sub: "sub-dup", email: "a@b.com");
        var first = Sample.AuthJson(idToken: token, refreshToken: "rt-1");
        var second = Sample.AuthJson(idToken: token, refreshToken: "rt-2");

        var p1 = env.Service.AddFromAuthJson(first, "Nome1");
        var p2 = env.Service.AddFromAuthJson(second, null);

        Assert.Equal(p1.Id, p2.Id);                // mesmo perfil (dedupe por sub)
        Assert.Single(env.Registry.Profiles);
    }

    [Fact]
    public void Remove_DeletesBlobAndMetadata()
    {
        using var env = new Env();
        var p = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "s")), "X");

        env.Service.Remove(p.Id);

        Assert.Empty(env.Registry.Profiles);
    }

    [Fact]
    public void Reload_PersistsAcrossInstances()
    {
        using var env = new Env();
        env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "s1")), "One");

        env.Service.Load(); // recarrega do disco

        Assert.Single(env.Registry.Profiles);
        Assert.Equal("One", env.Registry.Profiles[0].Nickname);
    }

    [Fact]
    public void MarkUsed_SetsTimestampToNow()
    {
        using var env = new Env();
        var p = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "s")), "X");

        env.Service.MarkUsed(p.Id);

        Assert.Equal(env.Clock.UtcNow, env.Registry.Find(p.Id)?.MarkedUsedAt);
    }

    [Fact]
    public void UnmarkUsed_ClearsTimestamp()
    {
        using var env = new Env();
        var p = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "s")), "X");
        env.Service.MarkUsed(p.Id);

        env.Service.UnmarkUsed(p.Id);

        Assert.Null(env.Registry.Find(p.Id)?.MarkedUsedAt);
    }

    [Fact]
    public void Reorder_AssignsSequentialSortOrder()
    {
        using var env = new Env();
        var a = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "a")), "A");
        var b = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "b")), "B");
        var c = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "c")), "C");

        env.Service.Reorder([c.Id, a.Id, b.Id]);

        Assert.Equal(0, env.Registry.Find(c.Id)?.SortOrder);
        Assert.Equal(1, env.Registry.Find(a.Id)?.SortOrder);
        Assert.Equal(2, env.Registry.Find(b.Id)?.SortOrder);
    }

    [Fact]
    public void Reorder_PersistsAcrossReload()
    {
        using var env = new Env();
        var a = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "a")), "A");
        var b = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "b")), "B");

        env.Service.Reorder([b.Id, a.Id]);
        env.Service.Load();

        Assert.Equal("B", env.Registry.Profiles.OrderBy(p => p.SortOrder).First().Nickname);
    }

    [Fact]
    public void ExportOne_ThenImport_RestoresAccountAndNickname()
    {
        using var source = new Env();
        var original = source.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "portable", email: "p@x.com")), "Portátil");
        var exported = source.Service.ExportOne(original.Id);

        using var destination = new Env();
        var count = destination.Service.Import(exported);

        var imported = Assert.Single(destination.Registry.Profiles);
        Assert.Equal(1, count);
        Assert.Equal("portable", imported.AccountSub);
        Assert.Equal("Portátil", imported.Nickname);
    }

    [Fact]
    public void ExportAll_ThenImport_RestoresAllAccountsInOrder()
    {
        using var source = new Env();
        var first = source.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "first")), "First");
        var second = source.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "second")), "Second");
        source.Service.Reorder([second.Id, first.Id]);

        using var destination = new Env();
        var count = destination.Service.Import(source.Service.ExportAll());

        Assert.Equal(2, count);
        Assert.Equal(["Second", "First"], destination.Registry.Profiles.OrderBy(p => p.SortOrder).Select(p => p.Nickname));
    }

    [Fact]
    public void Import_InvalidDocument_ThrowsWithoutChangingProfiles()
    {
        using var env = new Env();

        Assert.Throws<InvalidDataException>(() => env.Service.Import("{}"));
        Assert.Empty(env.Registry.Profiles);
    }

    [Fact]
    public void Import_RawAuthJson_AddsOneAccount()
    {
        using var env = new Env();
        var authJson = System.Text.Encoding.UTF8.GetString(
            Sample.AuthJson(idToken: Sample.Jwt(sub: "raw", email: "raw@x.com")));

        var count = env.Service.Import(authJson);

        Assert.Equal(1, count);
        Assert.Equal("raw", Assert.Single(env.Registry.Profiles).AccountSub);
    }

    [Fact]
    public void AccountRegistry_ReadsAreDefensiveSnapshots()
    {
        using var env = new Env();
        var profile = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "snapshot")), "Original");

        var listSnapshot = env.Registry.Profiles;
        var list = Assert.IsAssignableFrom<IList<CodexSwitcher.Core.Accounts.Models.ProfileMetadata>>(listSnapshot);
        Assert.True(list.IsReadOnly);
        Assert.NotSame(profile, listSnapshot[0]);
        listSnapshot[0].Nickname = "Changed through list";
        var findSnapshot = env.Registry.Find(profile.Id)!;
        findSnapshot.Nickname = "Changed through find";

        Assert.Equal("Original", env.Registry.Find(profile.Id)?.Nickname);
        Assert.DoesNotContain("Changed through", env.Service.ExportAll());
    }
}

public sealed class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Null_IsNever() => Assert.Equal("nunca", RelativeTime.Humanize(null, Now));

    [Fact]
    public void JustNow() => Assert.Equal("agora mesmo", RelativeTime.Humanize(Now.AddSeconds(-30), Now));

    [Fact]
    public void Minutes() => Assert.Equal("há 5 min", RelativeTime.Humanize(Now.AddMinutes(-5), Now));

    [Fact]
    public void Yesterday() => Assert.Equal("ontem", RelativeTime.Humanize(Now.AddDays(-1), Now));

    [Fact]
    public void Days() => Assert.Equal("há 3 dias", RelativeTime.Humanize(Now.AddDays(-3), Now));
}
