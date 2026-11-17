using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodexSwitcher.Infra.Common.Paths;

namespace CodexSwitcher.Infra.AgentState.Shadow;

internal sealed class ProfileMigrationLease : IAsyncDisposable
{
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ManualResetEventSlim _release;
    private readonly Task _worker;
    private int _disposed;

    internal bool WasAbandoned { get; }

    private ProfileMigrationLease(ManualResetEventSlim release, Task worker, bool wasAbandoned)
    {
        _release = release;
        _worker = worker;
        WasAbandoned = wasAbandoned;
    }

    internal static async Task<ProfileMigrationLease> AcquireAsync(AppPaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();

        var identity = Path.GetFullPath(paths.ProfilesPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var mutexName = $"CodexControlCenter-NicknameAuthority-{digest}";
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

                    if (ownsMutex)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            release.Set();
                            return;
                        }

                        acquired.TrySetResult(abandoned);
                        release.Wait();
                        return;
                    }
                }

                acquired.TrySetException(cancellationToken.IsCancellationRequested
                    ? new OperationCanceledException(cancellationToken)
                    : new TimeoutException("The nickname authority migration lease could not be acquired in time."));
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
            Name = "Nickname authority profile lease"
        };
        thread.Start();

        try
        {
            var wasAbandoned = await acquired.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ProfileMigrationLease(release, completed.Task, wasAbandoned);
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
