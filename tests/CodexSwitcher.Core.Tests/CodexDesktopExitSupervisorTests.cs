using System.Diagnostics;
using CodexSwitcher.Infra.Codex.Runtime;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexDesktopExitSupervisorTests
{
    [Fact]
    public void StopSignalsOnlyTheSelectedSessionWithTheExactSupervisorIdentity()
    {
        using var process = Process.GetCurrentProcess();
        var supervisor = new DesktopExitSupervisorIdentity(
            process.Id, process.StartTime.ToUniversalTime(), process.MainModule!.FileName);
        var sessionId = Guid.NewGuid();
        var otherSessionId = Guid.NewGuid();
        using var stop = new EventWaitHandle(
            false, EventResetMode.ManualReset, CodexDesktopExitSupervisor.StopName(sessionId));
        using var otherStop = new EventWaitHandle(
            false, EventResetMode.ManualReset, CodexDesktopExitSupervisor.StopName(otherSessionId));

        Assert.Null(CodexDesktopExitSupervisor.TryRequestStop(
            sessionId, supervisor with { StartedAt = supervisor.StartedAt.AddSeconds(-1) }));
        Assert.Null(CodexDesktopExitSupervisor.TryRequestStop(
            sessionId, supervisor with { ExecutablePath = "unrelated.exe" }));
        Assert.False(stop.WaitOne(0));

        using var verified = CodexDesktopExitSupervisor.TryRequestStop(sessionId, supervisor);
        Assert.Equal(process.Id, verified?.Id);
        Assert.True(stop.WaitOne(0));
        Assert.False(otherStop.WaitOne(0));
    }
}
