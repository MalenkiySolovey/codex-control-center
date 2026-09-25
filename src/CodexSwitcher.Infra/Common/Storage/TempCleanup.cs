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
using System.Diagnostics;
using System.Text.RegularExpressions;
namespace CodexSwitcher.Infra.Common.Storage;

/// <summary>
/// Limpeza das pastas efêmeras de perfis descartáveis (WebView2 <c>codex-webview-*</c> do login,
/// CODEX_HOME <c>codex-login-*</c> do login, e <c>codex-browser-*</c> de cada aba do navegador
/// privado) sob o TempRoot. O WebView2 grava cache/cookies/histórico no seu perfil isolado; apagamos
/// a pasta ao fechar, mas handles ainda presos ou arquivos read-only do plugin-clone do app-server
/// (.git) podem impedir a exclusão. Este helper força a exclusão (limpa atributos read-only) e varre
/// resíduos de sessões anteriores na inicialização.
/// </summary>
public static class TempCleanup
{
    private static readonly Regex WorkArtifactName = new(
        @"^(?:codex-(?:webview|login|browser)-[0-9a-f]{32}|usage-[0-9a-f]{32}-[0-9a-f]{32})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SystemTempArtifactName = new(
        @"^(?:codex_schema_|codex-catalog-val-|CodexSmoke_)[0-9a-f]{32}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AtomicWriteArtifactName = new(
        @"^.+\.tmp-[0-9a-f]{32}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AtomicWriteOwnedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "api-keys", "backups", "broker", "catalog", "catalogs", "keys", "totp", "vault",
    };

    public readonly record struct SweepResult(int Examined, int Removed, int Deferred);

    /// <summary>
    /// Compatibility helper for older callers. New startup code should use
    /// <see cref="SweepStaleOwnedArtifacts"/> with its captured process start time.
    /// </summary>
    public static void SweepLoginTemp(string tempRoot)
    {
        SweepOwnedRoot(tempRoot, WorkArtifactName, DateTime.UtcNow,
            maxCandidates: 256, maxEntriesPerDirectory: 20_000,
            maxDurationPerDirectory: TimeSpan.FromSeconds(2), maxRootEntries: 10_000,
            maxDurationPerRoot: TimeSpan.FromSeconds(2), cancellationToken: CancellationToken.None);
    }

    /// <summary>
    /// Performs crash recovery only for strictly Switchboard-owned directory names that existed
    /// before this process started. Work and system-temp roots are enumerated at their top level
    /// only. Processing is capped by candidate count, entry count, and elapsed time per directory.
    /// </summary>
    public static (SweepResult Work, SweepResult SystemTemp) SweepStaleOwnedArtifacts(
        string workRoot,
        string systemTempRoot,
        DateTimeOffset processStartedAt,
        int maxCandidatesPerRoot = 8,
        int maxEntriesPerDirectory = 20_000,
        TimeSpan? maxDurationPerDirectory = null,
        int maxEntriesPerRoot = 10_000,
        TimeSpan? maxDurationPerRoot = null,
        CancellationToken cancellationToken = default)
    {
        var staleBeforeUtc = processStartedAt.UtcDateTime;
        var duration = maxDurationPerDirectory ?? TimeSpan.FromSeconds(2);
        var rootDuration = maxDurationPerRoot ?? TimeSpan.FromSeconds(2);
        var work = SweepOwnedRoot(workRoot, WorkArtifactName, staleBeforeUtc,
            maxCandidatesPerRoot, maxEntriesPerDirectory, duration, maxEntriesPerRoot,
            rootDuration, cancellationToken);
        var systemTemp = SweepOwnedRoot(systemTempRoot, SystemTempArtifactName, staleBeforeUtc,
            maxCandidatesPerRoot, maxEntriesPerDirectory, duration, maxEntriesPerRoot,
            rootDuration, cancellationToken);
        return (work, systemTemp);
    }

    /// <summary>
    /// Recovers abandoned atomic-write siblings created by <see cref="PhysicalFileSystem"/>.
    /// The scan is limited to the app data root and known Switchboard-owned data directories;
    /// it skips reparse points and files touched after this process started.
    /// </summary>
    public static SweepResult SweepStaleAtomicWriteArtifacts(
        string appDataRoot,
        DateTimeOffset processStartedAt,
        int maxCandidates = 32,
        int maxEntries = 10_000,
        TimeSpan? maxDuration = null,
        int maxDepth = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appDataRoot) || maxCandidates <= 0 || maxEntries <= 0 || maxDepth < 0)
        {
            return default;
        }

        string root;
        try
        {
            root = Path.GetFullPath(appDataRoot);
            if (!Directory.Exists(root) || IsReparsePoint(root))
            {
                return default;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new SweepResult(0, 0, 1);
        }

        var stopwatch = Stopwatch.StartNew();
        var duration = maxDuration ?? TimeSpan.FromSeconds(1);
        var staleBeforeUtc = processStartedAt.UtcDateTime;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        var examined = 0;
        var removed = 0;
        var deferred = 0;
        var entries = 0;

        try
        {
            while (stack.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested || entries >= maxEntries || stopwatch.Elapsed >= duration)
                {
                    deferred++;
                    break;
                }

                var (directoryPath, depth) = stack.Pop();
                if (IsReparsePoint(directoryPath))
                {
                    continue;
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath, "*", SearchOption.TopDirectoryOnly))
                {
                    if (cancellationToken.IsCancellationRequested || entries >= maxEntries || stopwatch.Elapsed >= duration)
                    {
                        deferred++;
                        return new SweepResult(examined, removed, deferred);
                    }

                    entries++;
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var name = Path.GetFileName(entry);
                    if (isDirectory)
                    {
                        if (depth == 0)
                        {
                            if (AtomicWriteOwnedDirectories.Contains(name) && depth < maxDepth)
                            {
                                stack.Push((entry, depth + 1));
                            }
                        }
                        else if (depth < maxDepth)
                        {
                            stack.Push((entry, depth + 1));
                        }
                        continue;
                    }

                    if (!AtomicWriteArtifactName.IsMatch(name))
                    {
                        continue;
                    }

                    var info = new FileInfo(entry);
                    if (info.LastWriteTimeUtc >= staleBeforeUtc)
                    {
                        continue;
                    }

                    examined++;
                    if (examined > maxCandidates)
                    {
                        deferred++;
                        return new SweepResult(examined - 1, removed, deferred);
                    }

                    try
                    {
                        if ((attributes & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                        }
                        File.Delete(entry);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        deferred++;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            deferred++;
        }

        return new SweepResult(examined, removed, deferred);
    }

    private static SweepResult SweepOwnedRoot(
        string root,
        Regex ownedName,
        DateTime staleBeforeUtc,
        int maxCandidates,
        int maxEntriesPerDirectory,
        TimeSpan maxDurationPerDirectory,
        int maxRootEntries,
        TimeSpan maxDurationPerRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || maxCandidates <= 0 || maxRootEntries <= 0)
        {
            return default;
        }

        var examined = 0;
        var removed = 0;
        var deferred = 0;
        var rootEntries = 0;
        var rootStopwatch = Stopwatch.StartNew();
        try
        {
            foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (examined >= maxCandidates)
                {
                    break;
                }

                if (rootEntries >= maxRootEntries || rootStopwatch.Elapsed >= maxDurationPerRoot)
                {
                    deferred++;
                    break;
                }
                rootEntries++;

                var directory = new DirectoryInfo(path);
                if (!ownedName.IsMatch(directory.Name))
                {
                    continue;
                }

                DateTime createdUtc;
                try
                {
                    createdUtc = directory.CreationTimeUtc;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    deferred++;
                    continue;
                }

                if (createdUtc >= staleBeforeUtc)
                {
                    continue;
                }

                examined++;
                if (TryDeleteOwnedDirectoryBounded(directory.FullName, maxEntriesPerDirectory,
                        maxDurationPerDirectory, cancellationToken))
                {
                    removed++;
                }
                else
                {
                    deferred++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            deferred++;
        }

        return new SweepResult(examined, removed, deferred);
    }

    private static bool TryDeleteOwnedDirectoryBounded(
        string directoryPath,
        int maxEntries,
        TimeSpan maxDuration,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var stack = new Stack<(string Path, bool IsPostOrder)>();
        stack.Push((directoryPath, false));
        var entries = 0;

        try
        {
            while (stack.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested || entries >= maxEntries || stopwatch.Elapsed >= maxDuration)
                {
                    return false;
                }

                var (currentPath, isPostOrder) = stack.Pop();
                if (isPostOrder)
                {
                    Directory.Delete(currentPath, recursive: false);
                    entries++;
                    continue;
                }

                var attributes = File.GetAttributes(currentPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(currentPath, recursive: false);
                    entries++;
                    continue;
                }

                stack.Push((currentPath, true));
                foreach (var file in Directory.EnumerateFiles(currentPath, "*", SearchOption.TopDirectoryOnly))
                {
                    if (cancellationToken.IsCancellationRequested || entries >= maxEntries || stopwatch.Elapsed >= maxDuration)
                    {
                        return false;
                    }

                    var fileAttributes = File.GetAttributes(file);
                    if ((fileAttributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, fileAttributes & ~FileAttributes.ReadOnly);
                    }
                    File.Delete(file);
                    entries++;
                }

                foreach (var childDirectory in Directory.EnumerateDirectories(currentPath, "*", SearchOption.TopDirectoryOnly))
                {
                    if (cancellationToken.IsCancellationRequested || entries + stack.Count >= maxEntries || stopwatch.Elapsed >= maxDuration)
                    {
                        return false;
                    }
                    stack.Push((childDirectory, false));
                    entries++;
                }
            }

            return !Directory.Exists(directoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>Apaga a pasta recursivamente, limpando atributos read-only. Retorna true se sumiu.</summary>
    public static bool TryForceDelete(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return true;
        }

        if (!Directory.Exists(dir))
        {
            return true;
        }

        return TryDeleteOwnedDirectoryBounded(dir, maxEntries: 20_000,
            maxDuration: TimeSpan.FromSeconds(1), CancellationToken.None);
    }

}
