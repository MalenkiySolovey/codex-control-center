using CodexSwitcher.Infra.Codex.Runtime;

namespace CodexSwitcher.Core.Tests;

public sealed class GeneratedCatalogRetentionTests
{
    [Fact]
    public void Retention_prunes_only_stale_deleted_profiles_unreferenced_by_active_config()
    {
        var root = Path.Combine(Path.GetTempPath(), "switchboard-catalog-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var currentProfileId = Guid.NewGuid();
        var orphanProfileId = Guid.NewGuid();
        var activeOrphanProfileId = Guid.NewGuid();
        var current = CreateCatalog(root, currentProfileId, DateTime.UtcNow.AddMinutes(-10));
        var orphan = CreateCatalog(root, orphanProfileId, DateTime.UtcNow.AddMinutes(-10));
        var active = CreateCatalog(root, activeOrphanProfileId, DateTime.UtcNow.AddMinutes(-10));
        var activeCatalogPath = Path.Combine(active, "0123456789ab", "0123456789abcdef", "models.json");
        var unrelated = Path.Combine(root, "manual-content");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.json"), "keep");

        try
        {
            var result = GeneratedCatalogRetention.PruneDeletedProfileCatalogs(
                root,
                new HashSet<Guid> { currentProfileId },
                activeCatalogPath,
                DateTimeOffset.UtcNow.AddMinutes(-1));

            Assert.False(Directory.Exists(orphan));
            Assert.True(Directory.Exists(current));
            Assert.True(Directory.Exists(active));
            Assert.True(File.Exists(Path.Combine(unrelated, "keep.json")));
            Assert.Equal(1, result.Removed);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Retention_skips_profile_directories_with_unrecognized_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "switchboard-catalog-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var profileId = Guid.NewGuid();
        var candidate = CreateCatalog(root, profileId, DateTime.UtcNow.AddMinutes(-10));
        File.WriteAllText(Path.Combine(candidate, "user-note.txt"), "preserve");

        try
        {
            var result = GeneratedCatalogRetention.PruneDeletedProfileCatalogs(
                root,
                new HashSet<Guid>(),
                activeCatalogPath: null,
                DateTimeOffset.UtcNow.AddMinutes(-1));

            Assert.Equal(0, result.Removed);
            Assert.True(File.Exists(Path.Combine(candidate, "user-note.txt")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Retention_stops_scanning_after_the_configured_root_entry_budget()
    {
        var root = Path.Combine(Path.GetTempPath(), "switchboard-catalog-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var currentProfileIds = new HashSet<Guid>();
        for (var index = 0; index < 5; index++)
        {
            var profileId = Guid.NewGuid();
            currentProfileIds.Add(profileId);
            Directory.CreateDirectory(Path.Combine(root, profileId.ToString("D")));
        }

        try
        {
            var result = GeneratedCatalogRetention.PruneDeletedProfileCatalogs(
                root,
                currentProfileIds,
                activeCatalogPath: null,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                maxEntriesAtRoot: 1);

            Assert.Equal(0, result.Removed);
            Assert.Equal(1, result.Deferred);
            Assert.Equal(5, Directory.GetDirectories(root).Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Retention_keeps_all_candidates_when_the_active_catalog_reference_is_invalid()
    {
        var root = Path.Combine(Path.GetTempPath(), "switchboard-catalog-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var candidate = CreateCatalog(root, Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-10));

        try
        {
            var result = GeneratedCatalogRetention.PruneDeletedProfileCatalogs(
                root,
                new HashSet<Guid>(),
                activeCatalogPath: "\0",
                DateTimeOffset.UtcNow.AddMinutes(-1));

            Assert.Equal(0, result.Removed);
            Assert.Equal(1, result.Deferred);
            Assert.True(Directory.Exists(candidate));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string CreateCatalog(string root, Guid profileId, DateTime creationTimeUtc)
    {
        var profileDirectory = Path.Combine(root, profileId.ToString("D"));
        var catalog = Path.Combine(profileDirectory, "0123456789ab", "0123456789abcdef");
        Directory.CreateDirectory(catalog);
        File.WriteAllText(Path.Combine(catalog, "models.json"), "{\"models\":[]}");
        Directory.SetCreationTimeUtc(profileDirectory, creationTimeUtc);
        return profileDirectory;
    }
}
