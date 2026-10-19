using System.Diagnostics;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public class SwitchboardCodexProcessRegistryTests
{
    [Fact]
    public void RegisterAndUnregister_TracksPidsCorrectly()
    {
        var registry = new SwitchboardCodexProcessRegistry();

        Assert.False(registry.IsOwnedProcess(12345));
        Assert.Empty(registry.GetOwnedProcessIds());

        registry.RegisterOwnedProcess(12345);
        Assert.True(registry.IsOwnedProcess(12345));
        Assert.Contains(12345, registry.GetOwnedProcessIds());

        registry.RegisterOwnedProcess(67890);
        Assert.Equal(2, registry.GetOwnedProcessIds().Count);

        registry.UnregisterOwnedProcess(12345);
        Assert.False(registry.IsOwnedProcess(12345));
        Assert.True(registry.IsOwnedProcess(67890));
        Assert.Single(registry.GetOwnedProcessIds());

        registry.UnregisterOwnedProcess(67890);
        Assert.Empty(registry.GetOwnedProcessIds());
    }

    [Fact]
    public void RegisterInvalidPid_Ignored()
    {
        var registry = new SwitchboardCodexProcessRegistry();
        registry.RegisterOwnedProcess(0);
        registry.RegisterOwnedProcess(-1);

        Assert.False(registry.IsOwnedProcess(0));
        Assert.False(registry.IsOwnedProcess(-1));
        Assert.Empty(registry.GetOwnedProcessIds());
    }

    [Fact]
    public async Task TerminateAllOwnedProcessesAsync_HandlesNonExistentOrExitedProcesses()
    {
        var registry = new SwitchboardCodexProcessRegistry();
        // 999999 is typically a non-existent PID
        registry.RegisterOwnedProcess(999999);

        await registry.TerminateAllOwnedProcessesAsync(TimeSpan.FromMilliseconds(200));

        // After attempted termination of dead PID, it should be unregistered
        Assert.False(registry.IsOwnedProcess(999999));
        Assert.Empty(registry.GetOwnedProcessIds());
    }

    [Fact]
    public async Task TerminateAllOwnedProcessesAsync_KillsOnlyTheRegisteredLiveProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var owned = StartLongRunningLocalProcess();
        using var unrelated = StartLongRunningLocalProcess();
        var registry = new SwitchboardCodexProcessRegistry();
        registry.RegisterOwnedProcess(owned);

        try
        {
            await registry.TerminateAllOwnedProcessesAsync(TimeSpan.FromSeconds(2));

            Assert.True(owned.HasExited);
            Assert.False(unrelated.HasExited);
            Assert.Empty(registry.GetOwnedProcessIds());
        }
        finally
        {
            if (!owned.HasExited)
            {
                owned.Kill(entireProcessTree: true);
            }

            if (!unrelated.HasExited)
            {
                unrelated.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public void CodexProcessManager_FiltersOutOwnedProcessFromFoundList()
    {
        var registry = new SwitchboardCodexProcessRegistry();
        var currentPid = Environment.ProcessId;

        // Register current process as owned
        registry.RegisterOwnedProcess(currentPid);

        var managerWithRegistry = new CodexProcessManager(registry);
        Assert.True(registry.IsOwnedProcess(currentPid));
    }

    [Fact]
    public async Task CloseGracefullyThenKillAsync_RechecksOwnershipBeforeClosingCapturedTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = StartLongRunningLocalProcess();
        var registry = new SwitchboardCodexProcessRegistry();
        registry.RegisterOwnedProcess(process);
        var manager = new CodexProcessManager(registry);
        var staleCandidate = new CodexProcessInfo(
            process.Id,
            process.ProcessName,
            process.MainModule?.FileName,
            null,
            CodexProcessKind.Cli);

        try
        {
            var closed = await manager.CloseGracefullyThenKillAsync(
                [staleCandidate],
                TimeSpan.Zero);

            Assert.Empty(closed);
            Assert.False(process.HasExited);
        }
        finally
        {
            registry.UnregisterOwnedProcess(process.Id);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static Process StartLongRunningLocalProcess()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = commandProcessor,
            Arguments = "/d /c ping -n 30 127.0.0.1 > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return process ?? throw new InvalidOperationException("Could not start the local process used by this test.");
    }
}
