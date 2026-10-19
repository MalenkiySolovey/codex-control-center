using System.Diagnostics;

namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>
/// Thread-safe registry tracking child Codex app-server and CLI processes spawned by Codex Switchboard.
/// Enables excluding internal monitor processes from external user CLI remnant checks and facilitates
/// clean, bounded child process termination during application shutdown.
/// </summary>
public interface ISwitchboardCodexProcessRegistry
{
    /// <summary>
    /// Serializes process start-and-register windows with route-switch process discovery and shutdown.
    /// Hold the lease from immediately before Process.Start through RegisterOwnedProcess.
    /// </summary>
    IDisposable AcquireProcessLifecycleLease(CancellationToken cancellationToken = default);

    /// <summary>Asynchronously acquires the process lifecycle lease without blocking a UI thread.</summary>
    ValueTask<IDisposable> AcquireProcessLifecycleLeaseAsync(CancellationToken cancellationToken = default);

    /// <summary>Registers an owned child process ID and captures its creation time when available.</summary>
    void RegisterOwnedProcess(int processId);

    /// <summary>Registers a process instance directly so its identity is captured from the started process.</summary>
    void RegisterOwnedProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        RegisterOwnedProcess(process.Id);
    }

    /// <summary>Unregisters an owned child process ID when it completes or terminates.</summary>
    void UnregisterOwnedProcess(int processId);

    /// <summary>Determines whether the specified PID is owned by Switchboard.</summary>
    bool IsOwnedProcess(int processId);

    /// <summary>Returns a snapshot of all currently registered owned process IDs.</summary>
    IReadOnlyCollection<int> GetOwnedProcessIds();

    /// <summary>
    /// Returns registered process IDs and their current descendants. Desktop Electron windows
    /// spawn additional ChatGPT.exe processes that must remain excluded from route-switch scans.
    /// </summary>
    IReadOnlyCollection<int> GetOwnedProcessTreeIds() => GetOwnedProcessIds();

    /// <summary>
    /// Terminates all currently registered child processes within the specified bounded timeout.
    /// Operates only on registered child PIDs whose captured creation time still matches, never touching a
    /// process that reused a stale PID. Processes whose identity cannot be verified are left running.
    /// </summary>
    Task TerminateAllOwnedProcessesAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
