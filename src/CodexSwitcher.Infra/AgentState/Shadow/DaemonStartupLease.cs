using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodexSwitcher.Infra.AgentState.Shadow;

/// <summary>Serializes daemon startup attempts for one Control Center state directory.</summary>
internal sealed class DaemonStartupLease : IAsyncDisposable
{
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private readonly ManualResetEventSlim _release;
    private readonly Task _worker;
    private int _disposed;

    internal bool WasAbandoned { get; }

    private DaemonStartupLease(ManualResetEventSlim release, Task worker, bool wasAbandoned)
    {
        _release = release;
        _worker = worker;
        WasAbandoned = wasAbandoned;
    }

    internal static string GetMutexName(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var identity = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"CodexControlCenter-DaemonStartup-{digest}";
    }

    internal static async Task<DaemonStartupLease> AcquireAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mutexName = GetMutexName(dataDirectory);
        var options = new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true };
        var release = new ManualResetEventSlim(false);
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Mutex? mutex = null;
            var ownsMutex = false;
            try
            {
                mutex = new Mutex(false, mutexName, options, out _);
                var deadline = Stopwatch.StartNew();
                var abandoned = false;
                while (deadline.Elapsed < AcquisitionTimeout && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        ownsMutex = mutex.WaitOne(PollInterval);
                    }
                    catch (AbandonedMutexException)
                    {
                        ownsMutex = true;
                        abandoned = true;
                    }

                    if (!ownsMutex)
                        continue;

                    if (cancellationToken.IsCancellationRequested)
                    {
                        release.Set();
                        return;
                    }

                    acquired.TrySetResult(abandoned);
                    release.Wait();
                    return;
                }

                acquired.TrySetException(cancellationToken.IsCancellationRequested
                    ? new OperationCanceledException(cancellationToken)
                    : new TimeoutException("The daemon startup lease could not be acquired in time."));
            }
            catch (Exception exception)
            {
                acquired.TrySetException(exception);
            }
            finally
            {
                if (ownsMutex)
                {
                    try { mutex?.ReleaseMutex(); } catch (ApplicationException) { }
                }
                mutex?.Dispose();
                completed.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "Control Center daemon startup lease"
        };
        thread.Start();

        try
        {
            var wasAbandoned = await acquired.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new DaemonStartupLease(release, completed.Task, wasAbandoned);
        }
        catch
        {
            release.Set();
            try { await completed.Task.ConfigureAwait(false); } catch { }
            release.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _release.Set();
        await _worker.ConfigureAwait(false);
        _release.Dispose();
    }
}
