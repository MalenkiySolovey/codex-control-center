
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
namespace CodexSwitcher.Infra.Codex.Usage;

/// <summary>
/// Manages the lifecycle of an isolated temporary CODEX_HOME directory for a single profile.
/// Ensures credentials are isolated, ACL-hardened, tracked for token mutation, and reliably wiped upon completion.
/// Never interacts with or modifies the user's real %USERPROFILE%\.codex directory.
/// </summary>
public sealed class ProfileSandbox : IAsyncDisposable, IDisposable
{
    private readonly IFileSystem _fs;
    private readonly string _workRoot;
    private bool _disposed;

    public string DirectoryPath { get; }
    public string AuthJsonPath => Path.Combine(DirectoryPath, "auth.json");
    public string ConfigTomlPath => Path.Combine(DirectoryPath, "config.toml");
    public string InitialFingerprint { get; }

    private ProfileSandbox(string workRoot, string directoryPath, string initialFingerprint, IFileSystem fs)
    {
        _workRoot = workRoot;
        DirectoryPath = directoryPath;
        InitialFingerprint = initialFingerprint;
        _fs = fs;
    }

    /// <summary>
    /// Creates and initializes an isolated CODEX_HOME sandbox with the provided decrypted auth.json.
    /// </summary>
    public static ProfileSandbox Create(string workRoot, Guid profileId, byte[] authJsonBytes, IFileSystem fs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        ArgumentNullException.ThrowIfNull(authJsonBytes);
        ArgumentNullException.ThrowIfNull(fs);

        fs.CreateDirectory(workRoot);
        DirectoryHardening.RestrictToCurrentUser(workRoot);

        var sandboxDir = Path.Combine(workRoot, $"usage-{profileId:N}-{Guid.NewGuid():N}");
        try
        {
            fs.CreateDirectory(sandboxDir);
            DirectoryHardening.RestrictToCurrentUser(sandboxDir);

            var authPath = Path.Combine(sandboxDir, "auth.json");
            var configPath = Path.Combine(sandboxDir, "config.toml");

            fs.WriteAllBytesAtomic(authPath, authJsonBytes);
            fs.WriteAllTextAtomic(configPath, "cli_auth_credentials_store = \"file\"\n");

            var initialFingerprint = Fingerprint.Compute(authJsonBytes);
            return new ProfileSandbox(workRoot, sandboxDir, initialFingerprint, fs);
        }
        catch
        {
            // Construction failed before an owner could be returned to dispose the sandbox.
            // Remove partial credential/config files now; crash recovery remains the fallback
            // if Windows still holds a filesystem handle.
            try
            {
                TempCleanup.TryForceDelete(sandboxDir);
            }
            catch
            {
                // Preserve the creation failure; the next startup sweep retries stale sandboxes.
            }

            throw;
        }
    }

    /// <summary>
    /// Inspects the sandbox auth.json to determine if the CLI process mutated or rotated credentials.
    /// </summary>
    public (bool Mutated, byte[]? CurrentBytes, string? CurrentFingerprint) InspectMutation()
    {
        if (!_fs.FileExists(AuthJsonPath))
            return (false, null, null);

        try
        {
            var bytes = _fs.ReadAllBytes(AuthJsonPath);
            var currentFingerprint = Fingerprint.Compute(bytes);
            var mutated = !string.Equals(InitialFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase);
            return (mutated, bytes, currentFingerprint);
        }
        catch (IOException)
        {
            return (false, null, null);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        return new ValueTask(CleanupAsync());
    }

    private void Cleanup()
    {
        if (!Directory.Exists(DirectoryPath))
            return;

        // Safety verification: only delete directories strictly residing under the configured workRoot
        var fullDir = Path.GetFullPath(DirectoryPath);
        var fullRoot = Path.GetFullPath(_workRoot);
        if (!fullDir.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || fullDir.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Perform bounded, non-blocking cleanup without stalling application exit
        TempCleanup.TryForceDelete(DirectoryPath);
    }

    private async Task CleanupAsync()
    {
        if (!Directory.Exists(DirectoryPath))
            return;

        var fullDir = Path.GetFullPath(DirectoryPath);
        var fullRoot = Path.GetFullPath(_workRoot);
        if (!fullDir.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || fullDir.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The app-server can release SQLite/WAL handles just after its process exits.
        // Retry a couple of times off the UI thread rather than leaving a multi-megabyte
        // sandbox behind until a future launch performs crash recovery.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (TempCleanup.TryForceDelete(DirectoryPath))
                return;

            if (attempt < 2)
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1))).ConfigureAwait(false);
        }

        System.Diagnostics.Debug.WriteLine("[ProfileSandbox] cleanup deferred to startup recovery.");
    }
}
