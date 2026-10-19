using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexSwitcher.Core.Routing.Contracts;

namespace CodexSwitcher.Infra.Codex.Runtime;

/// <summary>
/// Thread-safe registry tracking child Codex app-server and CLI processes spawned by Codex Switchboard.
/// Ensures child processes are not mistaken for external user CLI instances and performs bounded,
/// best-effort shutdown without terminating a process that reused an old PID.
/// </summary>
public sealed class SwitchboardCodexProcessRegistry : ISwitchboardCodexProcessRegistry, IDisposable
{
    private const uint SnapshotProcessEntries = 0x00000002;
    private readonly ConcurrentDictionary<int, OwnedProcessIdentity> _ownedPids = new();
    private readonly SemaphoreSlim _processLifecycleGate = new(1, 1);

    public IDisposable AcquireProcessLifecycleLease(CancellationToken cancellationToken = default)
    {
        _processLifecycleGate.Wait(cancellationToken);
        return new ProcessLifecycleLease(_processLifecycleGate);
    }

    public async ValueTask<IDisposable> AcquireProcessLifecycleLeaseAsync(CancellationToken cancellationToken = default)
    {
        await _processLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessLifecycleLease(_processLifecycleGate);
    }

    public void Dispose() => _processLifecycleGate.Dispose();

    public void RegisterOwnedProcess(int processId)
    {
        if (processId > 0)
        {
            RegisterOwnedProcess(processId, TryGetStartTimeUtcTicks(processId));
        }
    }

    public void RegisterOwnedProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.Id > 0)
        {
            RegisterOwnedProcess(process.Id, TryGetStartTimeUtcTicks(process));
        }
    }

    public void UnregisterOwnedProcess(int processId)
    {
        _ownedPids.TryRemove(processId, out _);
    }

    public bool IsOwnedProcess(int processId) =>
        processId > 0 && _ownedPids.ContainsKey(processId);

    public IReadOnlyCollection<int> GetOwnedProcessIds() =>
        _ownedPids.Keys.ToArray();

    public IReadOnlyCollection<int> GetOwnedProcessTreeIds()
    {
        var processIds = GetVerifiedOwnedRoots();
        if (processIds.Count == 0 || !OperatingSystem.IsWindows())
        {
            return processIds;
        }

        var snapshot = CreateToolhelp32Snapshot(SnapshotProcessEntries, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return processIds;
        }

        try
        {
            var parentByProcessId = new Dictionary<int, int>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return processIds;
            }

            do
            {
                parentByProcessId[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (processId, parentId) in parentByProcessId)
                {
                    if (processIds.Contains(parentId) && processIds.Add(processId))
                    {
                        changed = true;
                    }
                }
            }
        }
        catch
        {
            // A snapshot can become invalid while processes are starting/exiting; retain
            // the registered roots so existing CLI process exclusion still works.
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return processIds;
    }

    public async Task TerminateAllOwnedProcessesAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var ownedProcesses = _ownedPids.ToArray();
        if (ownedProcesses.Length == 0)
            return;

        var tasks = ownedProcesses.Select(pair =>
            TerminateSingleProcessAsync(pair.Key, pair.Value, timeout, cancellationToken)).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task TerminateSingleProcessAsync(
        int pid,
        OwnedProcessIdentity identity,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                UnregisterOwnedProcess(pid);
                return;
            }

            // A PID can be reused after a child exits. Only terminate a process whose
            // creation time still matches the identity captured when it was registered.
            var currentStartTime = TryGetStartTimeUtcTicks(process);
            if (identity.StartTimeUtcTicks is not long expectedStartTime ||
                currentStartTime is not long actualStartTime)
            {
                return;
            }

            if (actualStartTime != expectedStartTime)
            {
                UnregisterOwnedProcess(pid);
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Headless app-server child processes cannot exit gracefully without stdin EOF/SIGINT;
                // terminate the process tree immediately for bounded shutdown.
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                if (HasExited(process))
                {
                    UnregisterOwnedProcess(pid);
                }

                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return;
            }

            using var timeoutCts = new CancellationTokenSource(
                totalTimeout > TimeSpan.Zero ? totalTimeout : TimeSpan.FromMilliseconds(1));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCts.Token,
                cancellationToken);
            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Keep the identity registered so a later shutdown pass can retry;
                // do not claim the child exited just because the bounded wait expired.
                return;
            }

            if (HasExited(process))
            {
                UnregisterOwnedProcess(pid);
            }
        }
        catch (ArgumentException)
        {
            UnregisterOwnedProcess(pid);
        }
        catch (InvalidOperationException)
        {
            if (process is not null && HasExited(process))
            {
                UnregisterOwnedProcess(pid);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access failures are not proof that the registered process exited.
        }
        finally
        {
            process?.Dispose();
        }
    }

    private HashSet<int> GetVerifiedOwnedRoots()
    {
        var verified = new HashSet<int>();
        foreach (var (processId, identity) in _ownedPids.ToArray())
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    UnregisterOwnedProcess(processId);
                    continue;
                }

                var currentStartTime = TryGetStartTimeUtcTicks(process);
                if (identity.StartTimeUtcTicks is long expectedStartTime &&
                    currentStartTime is long actualStartTime &&
                    expectedStartTime != actualStartTime)
                {
                    UnregisterOwnedProcess(processId);
                    continue;
                }

                // Unknown identities are retained only while no mismatch can be
                // established. Termination itself still requires a known match.
                if (identity.StartTimeUtcTicks is not null || currentStartTime is null)
                {
                    verified.Add(processId);
                }
                else
                {
                    UnregisterOwnedProcess(processId);
                }
            }
            catch (ArgumentException)
            {
                UnregisterOwnedProcess(processId);
            }
            catch (InvalidOperationException)
            {
                if (identity.StartTimeUtcTicks is not null)
                {
                    verified.Add(processId);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                if (identity.StartTimeUtcTicks is not null)
                {
                    verified.Add(processId);
                }
            }
            finally
            {
                process?.Dispose();
            }
        }

        return verified;
    }

    private void RegisterOwnedProcess(int processId, long? startTimeUtcTicks)
    {
        var identity = new OwnedProcessIdentity(startTimeUtcTicks);
        _ownedPids.AddOrUpdate(
            processId,
            identity,
            (_, current) => identity.StartTimeUtcTicks is not null ? identity : current);
    }

    private static long? TryGetStartTimeUtcTicks(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            return TryGetStartTimeUtcTicks(process);
        }
        catch
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static long? TryGetStartTimeUtcTicks(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return false; }
    }

    private sealed record OwnedProcessIdentity(long? StartTimeUtcTicks);

    private sealed class ProcessLifecycleLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is null)
            {
                return;
            }

            try { gate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
