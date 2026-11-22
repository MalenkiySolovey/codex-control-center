using System.Diagnostics;

namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>
/// Tracks owned Codex processes for route-switch exclusion and bounded shutdown of eligible children.
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

    /// <summary>
    /// Captures the started process's identity. Processes registered with force termination disabled
    /// still participate in route-switch exclusion and retain their dedicated shutdown policy.
    /// </summary>
    void RegisterOwnedProcess(Process process, bool allowForceTerminationOnShutdown = true);

    /// <summary>Unregisters an owned child process ID when it completes or terminates.</summary>
    void UnregisterOwnedProcess(int processId);

    /// <summary>Determines whether the specified PID is owned by Switchboard.</summary>
    bool IsOwnedProcess(int processId);

    /// <summary>Returns a snapshot of all currently registered owned process IDs.</summary>
    IReadOnlyCollection<int> GetOwnedProcessIds();

    /// <summary>
    /// Returns registered process IDs and their current descendants, including managed Desktop
    /// helpers that must remain excluded from route-switch scans.
    /// </summary>
    IReadOnlyCollection<int> GetOwnedProcessTreeIds() => GetOwnedProcessIds();

    /// <summary>
    /// Terminates registered children eligible for forced shutdown within the specified bounded timeout.
    /// The PID and captured creation time must match. Unverified processes are left running.
    /// </summary>
    Task TerminateAllOwnedProcessesAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
