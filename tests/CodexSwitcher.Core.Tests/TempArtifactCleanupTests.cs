using System.Diagnostics;
using CodexSwitcher.Infra.Common.Storage;

namespace CodexSwitcher.Core.Tests;

public sealed class TempArtifactCleanupTests
{
    [Fact]
    public void Sweep_removes_only_strictly_owned_artifacts_that_predate_this_process()
    {
        var root = Path.Combine(Path.GetTempPath(), "switchboard-cleanup-test-" + Guid.NewGuid().ToString("N"));
        var workRoot = Path.Combine(root, "work");
        var systemTemp = Path.Combine(root, "system-temp");
        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(systemTemp);
        var staleWork = Path.Combine(workRoot, $"usage-{Guid.NewGuid():N}-{Guid.NewGuid():N}");
        var liveWork = Path.Combine(workRoot, $"codex-login-{Guid.NewGuid():N}");
        var unrelatedWork = Path.Combine(workRoot, "usage-not-switchboard-owned");
        var staleSystem = Path.Combine(systemTemp, "CodexSmoke_" + Guid.NewGuid().ToString("N"));
        var unrelatedSystem = Path.Combine(systemTemp, "codex_schema_user-data");

        try
        {
            CreateOwnedDirectory(staleWork, DateTime.UtcNow.AddMinutes(-10));
            CreateOwnedDirectory(liveWork, DateTime.UtcNow);
            CreateOwnedDirectory(unrelatedWork, DateTime.UtcNow.AddMinutes(-10));
            CreateOwnedDirectory(staleSystem, DateTime.UtcNow.AddMinutes(-10));
            CreateOwnedDirectory(unrelatedSystem, DateTime.UtcNow.AddMinutes(-10));

            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
            var result = TempCleanup.SweepStaleOwnedArtifacts(workRoot, systemTemp, cutoff,
                maxCandidatesPerRoot: 16, maxEntriesPerDirectory: 200, maxDurationPerDirectory: TimeSpan.FromSeconds(2));

            Assert.False(Directory.Exists(staleWork));
            Assert.False(Directory.Exists(staleSystem));
            Assert.True(Directory.Exists(liveWork));
            Assert.True(Directory.Exists(unrelatedWork));
            Assert.True(Directory.Exists(unrelatedSystem));
            Assert.Equal(1, result.Work.Removed);
            Assert.Equal(1, result.SystemTemp.Removed);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Locked_artifact_is_deferred_without_hanging_or_deleting_unrelated_persistent_data()
    {
        var root = Path.Combine(Path.GetTempPath(), "switchboard-cleanup-test-" + Guid.NewGuid().ToString("N"));
        var workRoot = Path.Combine(root, "work");
        var systemTemp = Path.Combine(root, "system-temp");
        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(systemTemp);
        var lockedWork = Path.Combine(workRoot, $"usage-{Guid.NewGuid():N}-{Guid.NewGuid():N}");
        var profiles = Path.Combine(root, "profiles.json");
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(vault);
        File.WriteAllText(profiles, "persistent");
        var vaultData = Path.Combine(vault, "credential.bin");
        File.WriteAllText(vaultData, "persistent");

        try
        {
            CreateOwnedDirectory(lockedWork, DateTime.UtcNow.AddMinutes(-10));
            var lockedFile = Path.Combine(lockedWork, "locked.bin");
            using var stream = new FileStream(lockedFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var stopwatch = Stopwatch.StartNew();

            var result = TempCleanup.SweepStaleOwnedArtifacts(workRoot, systemTemp,
                DateTimeOffset.UtcNow.AddMinutes(-1), maxCandidatesPerRoot: 16,
                maxEntriesPerDirectory: 200, maxDurationPerDirectory: TimeSpan.FromSeconds(1));

            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(1, result.Work.Deferred);
            Assert.True(Directory.Exists(lockedWork));
            Assert.Equal("persistent", File.ReadAllText(profiles));
            Assert.Equal("persistent", File.ReadAllText(vaultData));
            stream.Dispose();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Canceled_sweep_does_not_touch_owned_artifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "switchboard-cleanup-test-" + Guid.NewGuid().ToString("N"));
        var workRoot = Path.Combine(root, "work");
        Directory.CreateDirectory(workRoot);
        var stale = Path.Combine(workRoot, $"codex-browser-{Guid.NewGuid():N}");

        try
        {
            CreateOwnedDirectory(stale, DateTime.UtcNow.AddMinutes(-10));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            TempCleanup.SweepStaleOwnedArtifacts(workRoot, Path.Combine(root, "absent"),
                DateTimeOffset.UtcNow.AddMinutes(-1), cancellationToken: cancellation.Token);

            Assert.True(Directory.Exists(stale));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Atomic_write_recovery_removes_only_stale_guid_siblings_under_owned_data_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "switchboard-atomic-cleanup-test-" + Guid.NewGuid().ToString("N"));
        var staleRootFile = Path.Combine(root, "profiles.json.tmp-" + Guid.NewGuid().ToString("N"));
        var catalogDirectory = Path.Combine(root, "catalogs", Guid.NewGuid().ToString("D"), "0123456789ab", "0123456789abcdef");
        var staleCatalogFile = Path.Combine(catalogDirectory, "models.json.tmp-" + Guid.NewGuid().ToString("N"));
        var liveFile = Path.Combine(root, "settings.json.tmp-" + Guid.NewGuid().ToString("N"));
        var malformedFile = Path.Combine(root, "settings.json.tmp-not-a-guid");
        var vaultFile = Path.Combine(root, "vault", "credential.bin");

        try
        {
            CreateAtomicTemp(staleRootFile, DateTime.UtcNow.AddMinutes(-10));
            CreateAtomicTemp(staleCatalogFile, DateTime.UtcNow.AddMinutes(-10));
            CreateAtomicTemp(liveFile, DateTime.UtcNow);
            CreateAtomicTemp(malformedFile, DateTime.UtcNow.AddMinutes(-10));
            Directory.CreateDirectory(Path.GetDirectoryName(vaultFile)!);
            File.WriteAllText(vaultFile, "persistent credential data");

            var result = TempCleanup.SweepStaleAtomicWriteArtifacts(
                root,
                DateTimeOffset.UtcNow.AddMinutes(-1));

            Assert.False(File.Exists(staleRootFile));
            Assert.False(File.Exists(staleCatalogFile));
            Assert.True(File.Exists(liveFile));
            Assert.True(File.Exists(malformedFile));
            Assert.Equal("persistent credential data", File.ReadAllText(vaultFile));
            Assert.Equal(2, result.Removed);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Atomic_write_recovery_defers_locked_files_and_stops_at_its_entry_budget()
    {
        var root = Path.Combine(Path.GetTempPath(), "switchboard-atomic-cleanup-test-" + Guid.NewGuid().ToString("N"));
        var lockedPath = Path.Combine(root, "settings.json.tmp-" + Guid.NewGuid().ToString("N"));
        var unrelatedOne = Path.Combine(root, "unrelated-one");
        var unrelatedTwo = Path.Combine(root, "unrelated-two");

        try
        {
            CreateAtomicTemp(lockedPath, DateTime.UtcNow.AddMinutes(-10));
            Directory.CreateDirectory(unrelatedOne);
            Directory.CreateDirectory(unrelatedTwo);
            using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var stopwatch = Stopwatch.StartNew();
            var result = TempCleanup.SweepStaleAtomicWriteArtifacts(
                root,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                maxEntries: 1);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.True(File.Exists(lockedPath));
            Assert.True(Directory.Exists(unrelatedOne));
            Assert.True(Directory.Exists(unrelatedTwo));
            Assert.True(result.Deferred >= 1);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void CreateOwnedDirectory(string path, DateTime creationTimeUtc)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "owned.txt"), "data");
        Directory.SetCreationTimeUtc(path, creationTimeUtc);
    }

    private static void CreateAtomicTemp(string path, DateTime lastWriteTimeUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "abandoned temp content");
        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
