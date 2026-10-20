using System.Diagnostics;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Security.Hardening;

namespace CodexSwitcher.Infra.AgentState.Shadow;

internal sealed class AccountStateShadowDaemonProcessManager : IAccountStateShadowClientFactory, IDisposable
{
    internal const string PipeName = "codex-switchboard-agent-state-shadow-v1";
    private static readonly TimeSpan InitialConnectTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(6);

    private readonly AccountStateShadowSettings _settings;
    private readonly string _dataDirectory;
    private readonly string _pipeName;
    private readonly object _processSync = new();
    private Process? _ownedProcess;
    private bool _disposed;

    public AccountStateShadowDaemonProcessManager(AccountStateShadowSettings settings, AppPaths paths, string pipeName = PipeName)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(pipeName) || !IsSupportedPipeName(pipeName))
            throw new ArgumentException("The shadow pipe name is invalid.", nameof(pipeName));
        _pipeName = pipeName;
        _dataDirectory = Path.Combine(paths.Root, "agent-state-shadow-v1");
    }

    public async Task<IAccountStateShadowClient> ConnectCompatibleAsync(CancellationToken cancellationToken)
    {
        lock (_processSync)
        {
            if (_disposed)
                throw new AccountStateShadowUnavailableException();
        }

        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.DaemonExecutablePath))
            throw new AccountStateShadowUnavailableException();

        EnsureShadowDataDirectory();
        var startedByThisCall = false;
        AccountStateShadowRpcClient? client = new(_pipeName);
        try
        {
            try
            {
                await client.ConnectAsync(InitialConnectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                client = null;
                startedByThisCall = EnsureOwnedProcessStarted(cancellationToken);
                client = await ConnectDuringStartupAsync(cancellationToken).ConfigureAwait(false);
            }

            await client.VerifyCompatibleAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (AccountStateShadowCompatibilityException)
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            if (startedByThisCall)
                await StopOwnedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopOwnedProcessAsync(CancellationToken cancellationToken)
    {
        Process? process;
        lock (_processSync)
        {
            process = _ownedProcess;
            _ownedProcess = null;
        }

        if (process is null)
            return;

        TryKill(process);

        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        Process? process;
        lock (_processSync)
        {
            _disposed = true;
            process = _ownedProcess;
            _ownedProcess = null;
        }

        if (process is null)
            return;

        TryKill(process);
        try
        {
            if (!process.HasExited)
                process.WaitForExit(200);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private void EnsureShadowDataDirectory()
    {
        if (!DirectoryHardening.TryRestrictToCurrentUser(_dataDirectory))
            throw new AccountStateShadowUnavailableException();
    }

    private bool EnsureOwnedProcessStarted(CancellationToken cancellationToken)
    {
        lock (_processSync)
        {
            if (_disposed)
                throw new AccountStateShadowUnavailableException();

            if (_ownedProcess is { HasExited: false })
                return false;

            _ownedProcess?.Dispose();
            _ownedProcess = null;
        }

        var executablePath = _settings.DaemonExecutablePath!;
        if (!File.Exists(executablePath))
            throw new AccountStateShadowUnavailableException();

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(_dataDirectory);
        startInfo.ArgumentList.Add("--pipe-name");
        startInfo.ArgumentList.Add(_pipeName);
        startInfo.ArgumentList.Add("--shadow-mode");

        cancellationToken.ThrowIfCancellationRequested();
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new AccountStateShadowUnavailableException();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            throw new AccountStateShadowUnavailableException();
        }

        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_processSync)
        {
            if (!_disposed)
            {
                _ownedProcess = process;
                return true;
            }
        }

        TryKill(process);
        try
        {
            if (!process.HasExited)
                process.WaitForExit(200);
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }

        throw new AccountStateShadowUnavailableException();
    }

    private async Task<AccountStateShadowRpcClient> ConnectDuringStartupAsync(CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_processSync)
            {
                if (_ownedProcess is { HasExited: true })
                    throw new AccountStateShadowUnavailableException();
            }

            var client = new AccountStateShadowRpcClient(_pipeName);
            try
            {
                await client.ConnectAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(75, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new AccountStateShadowUnavailableException();
    }

    private static bool IsSupportedPipeName(string pipeName)
    {
        if (string.Equals(pipeName, PipeName, StringComparison.Ordinal))
            return true;

        const string testPrefix = "agent-state-daemon-test-";
        return pipeName.StartsWith(testPrefix, StringComparison.Ordinal) && pipeName.Length == testPrefix.Length + 32 &&
            pipeName[testPrefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}

internal sealed class AccountStateShadowUnavailableException : Exception { }
