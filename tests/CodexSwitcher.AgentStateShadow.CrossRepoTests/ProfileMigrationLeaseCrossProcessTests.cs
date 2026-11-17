using System.Diagnostics;
using CodexSwitcher.Infra.AgentState.Shadow;
using CodexSwitcher.Infra.Common.Paths;
using Xunit;

namespace CodexSwitcher.AgentStateShadow.CrossRepoTests;

public sealed class ProfileMigrationLeaseCrossProcessTests
{
    private const string ChildProcessVariable = "CODEX_CC_NICKNAME_LEASE_CHILD";
    private const string ChildRootVariable = "CODEX_CC_NICKNAME_LEASE_ROOT";
    private const string ChildMarkerVariable = "CODEX_CC_NICKNAME_LEASE_MARKER";

    [Fact]
    public async Task NamedProfileLeaseBlocksASeparateProcessUntilTheOwnerReleasesIt()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The current-user/current-session migration mutex test requires Windows.");

        using var temp = new TemporaryDirectory();
        var sharedRoot = Path.Combine(temp.Path, "shared-switchboard");
        var paths = CreatePaths(sharedRoot);
        paths.EnsureDirectories();
        var markerPath = Path.Combine(temp.Path, "child-acquired-lease.marker");
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "tests", "CodexSwitcher.AgentStateShadow.CrossRepoTests", "CodexSwitcher.AgentStateShadow.CrossRepoTests.csproj");
        Assert.True(File.Exists(projectPath));

        Process child;
        bool acquiredWhileParentHeldLease;
        await using (var heldLease = await ProfileMigrationLease.AcquireAsync(paths, CancellationToken.None))
        {
            Assert.False(heldLease.WasAbandoned);
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = repositoryRoot
            };
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add("--configuration");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("--no-build");
            startInfo.ArgumentList.Add("--no-restore");
            startInfo.ArgumentList.Add("--filter");
            startInfo.ArgumentList.Add("FullyQualifiedName~ProfileMigrationLeaseChildProcessHelper");
            startInfo.ArgumentList.Add("--logger");
            startInfo.ArgumentList.Add("console;verbosity=minimal");
            startInfo.Environment[ChildProcessVariable] = "1";
            startInfo.Environment[ChildRootVariable] = sharedRoot;
            startInfo.Environment[ChildMarkerVariable] = markerPath;
            child = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the synthetic lease helper process.");

            await Task.Delay(900);
            acquiredWhileParentHeldLease = File.Exists(markerPath);
        }

        using (child)
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, child.ExitCode);
        }

        Assert.False(acquiredWhileParentHeldLease, "A second process entered the migration lease while the first owner still held it.");
        Assert.True(File.Exists(markerPath), "The second process did not acquire the lease after the owner released it.");
    }

    [Fact]
    public async Task ProfileMigrationLeaseChildProcessHelper()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(ChildProcessVariable), "1", StringComparison.Ordinal))
            return;

        var root = Environment.GetEnvironmentVariable(ChildRootVariable)
            ?? throw new InvalidOperationException("The synthetic lease root was not provided.");
        var marker = Environment.GetEnvironmentVariable(ChildMarkerVariable)
            ?? throw new InvalidOperationException("The synthetic lease marker was not provided.");
        var paths = CreatePaths(root);
        paths.EnsureDirectories();
        await using var lease = await ProfileMigrationLease.AcquireAsync(paths, CancellationToken.None);
        await File.WriteAllTextAsync(marker, "acquired");
    }

    private static AppPaths CreatePaths(string root) => new(
        root: root,
        codexHome: Path.Combine(root, "isolated-codex-home"),
        legacyRoot: Path.Combine(root, "isolated-legacy-root"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexSwitcher.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("The Control Center test repository root could not be located.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-switchboard-lease-cross-process", Guid.NewGuid().ToString("N"));
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
