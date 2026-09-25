using System.Diagnostics;
using System.Text.RegularExpressions;
using CodexSwitcher.Infra.Common.Storage;

namespace CodexSwitcher.Infra.Codex.Runtime;

/// <summary>Crash-safe retention for profile-scoped generated Codex catalogs.</summary>
public static class GeneratedCatalogRetention
{
    private static readonly Regex RuntimeFingerprint = new("^[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ContentFingerprint = new("^[0-9a-f]{16}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public readonly record struct SweepResult(int Examined, int Removed, int Deferred);

    /// <summary>
    /// Removes only old profile directories whose provider no longer exists, whose path is not
    /// referenced by active config.toml, and whose tree matches the generated catalog layout.
    /// All catalogs belonging to current (including archived) profiles remain untouched.
    /// </summary>
    public static SweepResult PruneDeletedProfileCatalogs(
        string catalogsRoot,
        IReadOnlySet<Guid> currentProfileIds,
        string? activeCatalogPath,
        DateTimeOffset processStartedAt,
        int maxCandidates = 4,
        int maxEntriesPerProfile = 10_000,
        TimeSpan? maxDurationPerProfile = null,
        int maxEntriesAtRoot = 10_000,
        TimeSpan? maxDurationAtRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogsRoot);
        ArgumentNullException.ThrowIfNull(currentProfileIds);
        if (maxCandidates <= 0 || !Directory.Exists(catalogsRoot))
        {
            return default;
        }

        var root = Path.GetFullPath(catalogsRoot);
        var cutoffUtc = processStartedAt.UtcDateTime;
        var duration = maxDurationPerProfile ?? TimeSpan.FromSeconds(2);
        var rootDuration = maxDurationAtRoot ?? TimeSpan.FromSeconds(2);
        var activePath = NormalizePath(activeCatalogPath);
        if (!string.IsNullOrWhiteSpace(activeCatalogPath) && activePath is null)
        {
            // If an active reference cannot be normalized, retaining all catalogs is safer than
            // guessing that a candidate is unreachable.
            return new SweepResult(0, 0, 1);
        }

        var examined = 0;
        var removed = 0;
        var deferred = 0;
        var rootEntries = 0;
        var rootStopwatch = Stopwatch.StartNew();

        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (cancellationToken.IsCancellationRequested || examined >= maxCandidates)
                {
                    break;
                }

                if (rootEntries >= maxEntriesAtRoot || rootStopwatch.Elapsed >= rootDuration)
                {
                    deferred++;
                    break;
                }
                rootEntries++;

                var directory = new DirectoryInfo(candidate);
                if (!Guid.TryParseExact(directory.Name, "D", out var profileId) || currentProfileIds.Contains(profileId))
                {
                    continue;
                }

                if (IsReparsePoint(candidate))
                {
                    continue;
                }

                if (directory.CreationTimeUtc >= cutoffUtc || IsActiveCatalogUnder(candidate, activePath))
                {
                    continue;
                }

                examined++;
                if (!HasOnlyGeneratedCatalogFiles(candidate, maxEntriesPerProfile, duration, cancellationToken))
                {
                    deferred++;
                    continue;
                }

                try
                {
                    if (TempCleanup.TryForceDelete(candidate))
                    {
                        removed++;
                    }
                    else
                    {
                        deferred++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

    private static bool HasOnlyGeneratedCatalogFiles(
        string profileDirectory,
        int maxEntries,
        TimeSpan maxDuration,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var entries = 0;
        try
        {
            foreach (var runtimeDirectory in Directory.EnumerateFileSystemEntries(profileDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (HasExceededBudget(stopwatch, maxDuration, entries, maxEntries, cancellationToken)
                    || !Directory.Exists(runtimeDirectory)
                    || !RuntimeFingerprint.IsMatch(Path.GetFileName(runtimeDirectory))
                    || IsReparsePoint(runtimeDirectory))
                {
                    return false;
                }
                entries++;

                foreach (var contentDirectory in Directory.EnumerateFileSystemEntries(runtimeDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (HasExceededBudget(stopwatch, maxDuration, entries, maxEntries, cancellationToken)
                        || !Directory.Exists(contentDirectory)
                        || !ContentFingerprint.IsMatch(Path.GetFileName(contentDirectory))
                        || IsReparsePoint(contentDirectory))
                    {
                        return false;
                    }
                    entries++;

                    foreach (var entry in Directory.EnumerateFileSystemEntries(contentDirectory, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (HasExceededBudget(stopwatch, maxDuration, entries, maxEntries, cancellationToken))
                        {
                            return false;
                        }

                        if (Directory.Exists(entry) || IsReparsePoint(entry)
                            || !string.Equals(Path.GetFileName(entry), "models.json", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                        entries++;
                    }
                }
            }

            return entries > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasExceededBudget(
        Stopwatch stopwatch,
        TimeSpan maxDuration,
        int entries,
        int maxEntries,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested || stopwatch.Elapsed >= maxDuration || entries >= maxEntries;

    private static bool IsActiveCatalogUnder(string profileDirectory, string? activePath)
    {
        if (activePath is null)
        {
            return false;
        }

        var prefix = Path.GetFullPath(profileDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return activePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
