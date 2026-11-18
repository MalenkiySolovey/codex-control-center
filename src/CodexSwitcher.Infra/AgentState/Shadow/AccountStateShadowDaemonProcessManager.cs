using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Security.Hardening;

namespace CodexSwitcher.Infra.AgentState.Shadow;

internal sealed class AccountStateShadowDaemonProcessManager : IAccountStateShadowClientFactory, IDisposable
{
    internal const string PipeName = "codex-switchboard-agent-state-shadow-v1";
    internal const string NicknameAuthorityPipeName = "codex-switchboard-agent-state-nickname-authority-v1";
    private static readonly TimeSpan InitialConnectTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(6);

    private readonly AccountStateShadowSettings _settings;
    private readonly NicknameAuthorityCanarySettings _authoritySettings;
    private readonly NicknameAuthorityMigrationJournal? _nicknameJournal;
    private readonly string _dataDirectory;
    private readonly string _pipeName;
    private readonly string _authorityPipeName;
    private readonly object _processSync = new();
    private Process? _ownedProcess;
    private DaemonMode? _ownedMode;
    // Once authority was reachable in this app session, an unexpected disconnect is an offline
    // condition. Do not silently spawn a replacement from a nickname write/export request.
    private bool _authorityConnectionEstablished;
    private bool _disposed;
    private int _lastConnectionHealthy;

    public AccountStateShadowDaemonProcessManager(
        AccountStateShadowSettings settings,
        AppPaths paths,
        string pipeName = PipeName,
        NicknameAuthorityCanarySettings? authoritySettings = null,
        NicknameAuthorityMigrationJournal? nicknameJournal = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _authoritySettings = authoritySettings ?? NicknameAuthorityCanarySettings.FromEnvironment();
        _nicknameJournal = nicknameJournal;
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(pipeName) || !IsSupportedPipeName(pipeName))
            throw new ArgumentException("The shadow pipe name is invalid.", nameof(pipeName));
        _dataDirectory = Path.Combine(paths.Root, "agent-state-shadow-v1");
        var usesDefaultRoot = string.Equals(
            Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(AppPaths.DefaultRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        _pipeName = string.Equals(pipeName, PipeName, StringComparison.Ordinal) && !usesDefaultRoot
            ? CreateRootScopedPipeName(_dataDirectory, "shadow")
            : pipeName;
        _authorityPipeName = usesDefaultRoot
            ? NicknameAuthorityPipeName
            : CreateRootScopedPipeName(_dataDirectory, "authority");
    }

    internal string AuthorityPipeName => _authorityPipeName;

    public async Task<IAccountStateShadowClient> ConnectCompatibleAsync(CancellationToken cancellationToken)
    {
        lock (_processSync)
        {
            if (_disposed)
                throw new AccountStateShadowUnavailableException();

            if (!MayRunLegacyShadowMode())
                throw new AccountStateShadowUnavailableException();
        }

        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.DaemonExecutablePath))
            throw new AccountStateShadowUnavailableException();

        EnsureShadowDataDirectory();
        var startedByThisCall = false;
        AccountStateShadowRpcClient? client = new(_pipeName);
        var verifiedDuringStartup = false;
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
                var connection = await ConnectAfterStartupLeaseAsync(DaemonMode.Shadow, _pipeName, cancellationToken).ConfigureAwait(false);
                client = connection.Client;
                startedByThisCall = connection.StartedByThisCall;
                verifiedDuringStartup = true;
            }

            if (!verifiedDuringStartup)
            {
                if (await IsCompetingDaemonReachableAsync(DaemonMode.Shadow, cancellationToken).ConfigureAwait(false))
                    throw new AccountStateShadowUnavailableException();
                await client.VerifyCompatibleAsync(cancellationToken).ConfigureAwait(false);
            }
            Volatile.Write(ref _lastConnectionHealthy, 1);
            return client;
        }
        catch (AccountStateShadowCompatibilityException)
        {
            Volatile.Write(ref _lastConnectionHealthy, 0);
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            if (startedByThisCall)
                await StopOwnedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            Volatile.Write(ref _lastConnectionHealthy, 0);
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool MayRunLegacyShadowMode()
    {
        if (_nicknameJournal?.Exists != true)
            return true;
        try
        {
            var journal = _nicknameJournal.Read();
            return journal is null ||
                (!journal.AuthorityWasActivated && journal.AuthorityState == NicknameAuthorityStates.LegacyLocal && journal.MigrationId is null);
        }
        catch
        {
            return false;
        }
    }

    public async Task<IAccountStateShadowClient> ConnectNicknameAuthorityCompatibleAsync(CancellationToken cancellationToken)
    {
        lock (_processSync)
        {
            if (_disposed)
                throw new AccountStateShadowUnavailableException();
        }
        EnsureShadowDataDirectory();
        var startedByThisCall = false;
        AccountStateShadowRpcClient? client = new(_authorityPipeName);
        var verifiedDuringStartup = false;
        try
        {
            DaemonMode? initialOwnedMode;
            lock (_processSync)
                initialOwnedMode = _ownedMode;
            if (initialOwnedMode == DaemonMode.Shadow)
                await StopOwnedProcessAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await client.ConnectAsync(InitialConnectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                client = null;

                if (await IsLegacyDaemonReachableAsync(cancellationToken).ConfigureAwait(false))
                    throw new AccountStateShadowUnavailableException();

                bool authorityWasAlreadyConnected;
                lock (_processSync)
                    authorityWasAlreadyConnected = _authorityConnectionEstablished;
                if (authorityWasAlreadyConnected)
                    throw new AccountStateShadowUnavailableException();

                if (!_authoritySettings.CanStartDaemon)
                    throw new AccountStateShadowUnavailableException();

                var connection = await ConnectAfterStartupLeaseAsync(DaemonMode.NicknameAuthority, _authorityPipeName, cancellationToken).ConfigureAwait(false);
                client = connection.Client;
                startedByThisCall = connection.StartedByThisCall;
                verifiedDuringStartup = true;
            }

            if (!verifiedDuringStartup)
            {
                if (await IsCompetingDaemonReachableAsync(DaemonMode.NicknameAuthority, cancellationToken).ConfigureAwait(false))
                    throw new AccountStateShadowUnavailableException();
                await client.VerifyNicknameAuthorityCompatibleAsync(cancellationToken).ConfigureAwait(false);
            }
            lock (_processSync)
                _authorityConnectionEstablished = true;
            Volatile.Write(ref _lastConnectionHealthy, 1);
            return client;
        }
        catch (AccountStateShadowCompatibilityException)
        {
            Volatile.Write(ref _lastConnectionHealthy, 0);
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            if (startedByThisCall)
                await StopOwnedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            Volatile.Write(ref _lastConnectionHealthy, 0);
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IAccountStateShadowClient?> TryConnectExistingNicknameAuthorityAsync(CancellationToken cancellationToken)
    {
        lock (_processSync)
        {
            if (_disposed)
                throw new AccountStateShadowUnavailableException();
        }

        var client = new AccountStateShadowRpcClient(_authorityPipeName);
        try
        {
            await client.ConnectAsync(InitialConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        try
        {
            DaemonMode? ownedMode;
            lock (_processSync)
                ownedMode = _ownedMode;
            if (ownedMode == DaemonMode.Shadow)
                await StopOwnedProcessAsync(cancellationToken).ConfigureAwait(false);

            await client.VerifyNicknameAuthorityCompatibleAsync(cancellationToken).ConfigureAwait(false);
            lock (_processSync)
                _authorityConnectionEstablished = true;
            Volatile.Write(ref _lastConnectionHealthy, 1);
            return client;
        }
        catch
        {
            Volatile.Write(ref _lastConnectionHealthy, 0);
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public bool IsLastConnectionHealthy
    {
        get => Volatile.Read(ref _lastConnectionHealthy) != 0;
    }

    internal int? OwnedProcessId
    {
        get
        {
            lock (_processSync)
            {
                try
                {
                    return _ownedProcess is { HasExited: false } process ? process.Id : null;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }
    }

    public async Task StopOwnedProcessAsync(CancellationToken cancellationToken)
    {
        Process? process;
        lock (_processSync)
        {
            process = _ownedProcess;
            _ownedProcess = null;
            _ownedMode = null;
            // This is an explicit lifecycle stop (for example, app shutdown or a mode switch).
            // An unexpected process exit does not go through this path and stays latched offline.
            _authorityConnectionEstablished = false;
            Volatile.Write(ref _lastConnectionHealthy, 0);
        }

        if (process is null)
            return;

        TryKill(process);

        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public Task StopOwnedLegacyProcessAsync(CancellationToken cancellationToken)
    {
        lock (_processSync)
        {
            if (_ownedMode != DaemonMode.Shadow)
                return Task.CompletedTask;
        }
        return StopOwnedProcessAsync(cancellationToken);
    }

    public void Dispose()
    {
        Process? process;
        lock (_processSync)
        {
            _disposed = true;
            process = _ownedProcess;
            _ownedProcess = null;
            _ownedMode = null;
            Volatile.Write(ref _lastConnectionHealthy, 0);
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

    private async Task<(AccountStateShadowRpcClient Client, bool StartedByThisCall)> ConnectAfterStartupLeaseAsync(
        DaemonMode mode,
        string targetPipe,
        CancellationToken cancellationToken)
    {
        await using var startupLease = await DaemonStartupLease.AcquireAsync(_dataDirectory, cancellationToken).ConfigureAwait(false);
        if (await IsCompetingDaemonReachableAsync(mode, cancellationToken).ConfigureAwait(false))
            throw new AccountStateShadowUnavailableException();

        var client = await TryConnectPipeAsync(targetPipe, cancellationToken).ConfigureAwait(false);
        var startedByThisCall = false;
        try
        {
            if (client is null)
            {
                if (mode == DaemonMode.Shadow && (!_settings.Enabled || !MayRunLegacyShadowMode()))
                    throw new AccountStateShadowUnavailableException();
                if (mode == DaemonMode.NicknameAuthority && !_authoritySettings.CanStartDaemon)
                    throw new AccountStateShadowUnavailableException();

                startedByThisCall = EnsureOwnedProcessStarted(mode, cancellationToken);
                client = mode == DaemonMode.Shadow
                    ? await ConnectDuringStartupAsync(cancellationToken).ConfigureAwait(false)
                    : await ConnectAuthorityDuringStartupAsync(cancellationToken).ConfigureAwait(false);
            }

            if (mode == DaemonMode.Shadow)
                await client.VerifyCompatibleAsync(cancellationToken).ConfigureAwait(false);
            else
                await client.VerifyNicknameAuthorityCompatibleAsync(cancellationToken).ConfigureAwait(false);
            return (client, startedByThisCall);
        }
        catch
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            if (startedByThisCall)
                await StopOwnedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> IsCompetingDaemonReachableAsync(DaemonMode requestedMode, CancellationToken cancellationToken)
    {
        if (requestedMode == DaemonMode.NicknameAuthority)
            return await IsLegacyDaemonReachableAsync(cancellationToken).ConfigureAwait(false);

        await using var authority = await TryConnectPipeAsync(_authorityPipeName, cancellationToken).ConfigureAwait(false);
        return authority is not null;
    }

    private static async Task<AccountStateShadowRpcClient?> TryConnectPipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        var client = new AccountStateShadowRpcClient(pipeName);
        try
        {
            await client.ConnectAsync(InitialConnectTimeout, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    private bool EnsureOwnedProcessStarted(DaemonMode mode, CancellationToken cancellationToken)
    {
        lock (_processSync)
        {
            if (_disposed)
                throw new AccountStateShadowUnavailableException();

            if (mode == DaemonMode.Shadow && !MayRunLegacyShadowMode())
                throw new AccountStateShadowUnavailableException();

            if (_ownedProcess is { HasExited: false })
            {
                if (_ownedMode != mode)
                    throw new AccountStateShadowUnavailableException();
                return false;
            }

            _ownedProcess?.Dispose();
            _ownedProcess = null;
            _ownedMode = null;
            Volatile.Write(ref _lastConnectionHealthy, 0);

            var executablePath = mode == DaemonMode.Shadow
                ? _settings.DaemonExecutablePath
                : _authoritySettings.DaemonExecutablePath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
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
            startInfo.ArgumentList.Add(mode == DaemonMode.Shadow ? _pipeName : _authorityPipeName);
            startInfo.ArgumentList.Add(mode == DaemonMode.Shadow ? "--shadow-mode" : "--experimental-nickname-authority");

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

            try
            {
                process.OutputDataReceived += static (_, _) => { };
                process.ErrorDataReceived += static (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _ownedProcess = process;
                _ownedMode = mode;
                return true;
            }
            catch
            {
                TryKill(process);
                process.Dispose();
                throw;
            }
        }
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

    private async Task<AccountStateShadowRpcClient> ConnectAuthorityDuringStartupAsync(CancellationToken cancellationToken)
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

            var client = new AccountStateShadowRpcClient(_authorityPipeName);
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

    private async Task<bool> IsLegacyDaemonReachableAsync(CancellationToken cancellationToken)
    {
        var legacy = new AccountStateShadowRpcClient(_pipeName);
        try
        {
            await legacy.ConnectAsync(InitialConnectTimeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or AccountStateShadowCompatibilityException or AccountStateShadowRpcException)
        {
            return false;
        }
        finally
        {
            await legacy.DisposeAsync().ConfigureAwait(false);
        }
    }

    private enum DaemonMode
    {
        Shadow,
        NicknameAuthority
    }

    private static bool IsSupportedPipeName(string pipeName)
    {
        if (string.Equals(pipeName, PipeName, StringComparison.Ordinal))
            return true;

        const string testPrefix = "agent-state-daemon-test-";
        return pipeName.StartsWith(testPrefix, StringComparison.Ordinal) && pipeName.Length == testPrefix.Length + 32 &&
            pipeName[testPrefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static string CreateRootScopedPipeName(string dataDirectory, string mode)
    {
        var identity = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant() + "|" + mode;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"agent-state-daemon-test-{digest[..32]}";
    }
}

internal sealed class AccountStateShadowUnavailableException : Exception { }
