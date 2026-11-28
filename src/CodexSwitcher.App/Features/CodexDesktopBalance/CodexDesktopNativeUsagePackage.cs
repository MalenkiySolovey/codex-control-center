using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codex.RuntimeKit.Desktop.Discovery;
using Codex.RuntimeKit.Desktop.Extensions;

namespace CodexSwitcher.App.Features.CodexDesktopBalance;

/// <summary>Derives the usage extension from the Store package of the managed renderer.</summary>
internal sealed partial class CodexDesktopNativeUsagePackage
{
    internal const string Id = "codex-control-center-api-balance-native";
    internal const string RootAttribute = "data-codex-control-center-api-balance-native";
    private const string FactorySlot = "/* CODEX_NATIVE_USAGE_FACTORY */ null";
    private const string RuntimeSlot = "/* CODEX_NATIVE_USAGE_RUNTIME */ null";
    private const string ResourceName = "CodexSwitcher.App.Features.CodexDesktopBalance.CodexDesktopNativeUsage.js";
    private const int MaximumAssetBytes = 4 * 1024 * 1024;
    private const long MaximumScanBytes = 512L * 1024 * 1024;
    private static readonly Version RecipeVersion = new(2, 3, 0);
    private static readonly UTF8Encoding SourceEncoding = new(false, true);
    private readonly Dictionary<ArchiveIdentity, Resolution> _recipes = [];

    internal enum State { Ready, UnsupportedContract, AmbiguousContract, CorruptSource, Unavailable }

    internal sealed record Resolution(State State, CodexDesktopExtensionPackage? Package = null, string? IndexSha256 = null);

    internal sealed record ScriptResult(State State, string? Script = null);

    public async Task<Resolution> ResolveAsync(CodexDesktopInstallation installation, CancellationToken cancellationToken)
    {
        try
        {
            var archivePath = Path.Combine(Path.GetDirectoryName(installation.ExecutablePath)!, "resources", "app.asar");
            var info = new FileInfo(archivePath);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                (info.Directory!.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new(State.Unavailable);
            }

            await using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var index = await ReadIndexAsync(archive, cancellationToken).ConfigureAwait(false);
            if (index is null) return new(State.CorruptSource);

            var identity = new ArchiveIdentity(archivePath, installation.Version, RecipeVersion, archive.Length, info.LastWriteTimeUtc, index.Sha256);
            if (_recipes.TryGetValue(identity, out var cached))
            {
                return cached;
            }

            var package = CodexDesktopBalanceInstallationResolver.FindStorePackage(installation.InstallLocation);
            if (package is null) return new(State.Unavailable);
            if (!await package.VerifyContentIntegrityAsync().AsTask(cancellationToken).ConfigureAwait(false))
            {
                return new(State.CorruptSource);
            }

            var source = await BuildScriptAsync(archive, index, cancellationToken).ConfigureAwait(false);
            var extension = source.Script is not { } script ? null : new CodexDesktopExtensionPackage(
                Id, RecipeVersion, installation.Version, installation.Version,
                Convert.ToHexString(SHA256.HashData(SourceEncoding.GetBytes(script))), script)
            {
                RestorePayloadOnReload = false,
            };

            if (_recipes.Count >= 4) _recipes.Clear();
            var result = new Resolution(source.State, extension, index.Sha256);
            _recipes[identity] = result;
            return result;
        }
        catch (Exception exception) when (IsCorruptArchive(exception))
        {
            return new(State.CorruptSource);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            return new(State.Unavailable);
        }
    }

    // Archive and renderer identities are verified by the production caller. This method only
    // reads the archive and builds the adapter, which also lets fixtures exercise the full discovery path.
    internal static async Task<ScriptResult> InspectArchiveAsync(Stream archive, CancellationToken cancellationToken)
    {
        try
        {
            using var index = await ReadIndexAsync(archive, cancellationToken).ConfigureAwait(false);
            return index is null ? new(State.CorruptSource) : await BuildScriptAsync(archive, index, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCorruptArchive(exception))
        {
            return new(State.CorruptSource);
        }
    }

    private static async Task<ScriptResult> BuildScriptAsync(Stream archive, ArchiveIndex index, CancellationToken cancellationToken)
    {
        var (dataStart, json, _) = index;
        if (!TryGetFiles(json.RootElement, out var root) || !root.TryGetProperty("webview", out var webview) ||
            !TryGetFiles(webview, out var web) || !web.TryGetProperty("index.html", out var htmlEntry) ||
            !web.TryGetProperty("assets", out var assetsEntry) || !TryGetFiles(assetsEntry, out var assets))
        {
            return new(State.UnsupportedContract);
        }

        if (!TryReadAsset(htmlEntry, dataStart, archive.Length, out var html)) return new(State.CorruptSource);
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumAssetBytes);
        try
        {
            await ReadAssetAsync(archive, html, buffer, cancellationToken).ConfigureAwait(false);
            if (!MatchesIntegrity(htmlEntry, buffer.AsSpan(0, html.Size))) return new(State.CorruptSource);

            var entries = ModuleScript().Matches(SourceEncoding.GetString(buffer, 0, html.Size))
                .ToLookup(match => match.Groups["src"].Value, StringComparer.Ordinal);
            var (state, runtime, usage, dom) = await DiscoverAsync(archive, dataStart, entries, assets, buffer, cancellationToken).ConfigureAwait(false);
            if (runtime is null || usage is null || dom is null) return new(state);

            using var resource = typeof(CodexDesktopNativeUsagePackage).Assembly.GetManifestResourceStream(ResourceName);
            if (resource is null) return new(State.Unavailable);

            using var reader = new StreamReader(resource, SourceEncoding);
            var template = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).ReplaceLineEndings("\n");
            var scripts = JsonSerializer.Serialize(new
            {
                entry = runtime,
                usage = usage.Reference.Id,
                dom = dom.Id,
                chunks = usage.Reference.Chunks.Concat(dom.Chunks).Distinct().ToArray(),
            });
            var script = template.Replace(FactorySlot, usage.Factory, StringComparison.Ordinal)
                .Replace(RuntimeSlot, scripts, StringComparison.Ordinal);
            return SourceEncoding.GetByteCount(script) <= CodexDesktopExtensionHost.MaximumScriptBytes
                ? new(State.Ready, script)
                : new(State.UnsupportedContract);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<(State State, string? Runtime, CodexDesktopNativeUsageRecipe.UsageModule? Usage, CodexDesktopNativeUsageRecipe.ModuleReference? Dom)>
        DiscoverAsync(Stream archive, long dataStart, ILookup<string, Match> entries, JsonElement files, byte[] buffer, CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return (State.UnsupportedContract, null, null, null);
        string? runtime = null;
        var usage = (CodexDesktopNativeUsageRecipe.UsageModule?)null;
        var dom = (CodexDesktopNativeUsageRecipe.ModuleReference?)null;
        long scanned = 0;
        var packedScripts = from item in EnumerateAssets(files, "/assets/", 0)
                            where item.Path.EndsWith(".js", StringComparison.Ordinal)
                            let asset = ReadAssetLocation(item.Entry, dataStart, archive.Length)
                            where asset.HasValue
                            let location = asset.GetValueOrDefault()
                            orderby location.Offset
                            select (item.Path, item.Entry, Asset: location);
        foreach (var (relativePath, entry, asset) in packedScripts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned += asset.Size;
            if (scanned > MaximumScanBytes) return (State.UnsupportedContract, null, null, null);

            await ReadAssetAsync(archive, asset, buffer, cancellationToken).ConfigureAwait(false);
            var bytes = buffer.AsSpan(0, asset.Size);
            var runtimeCandidate = entries.Contains(relativePath) && bytes.IndexOf("__webpack_require__"u8) >= 0;
            var usageCandidate = bytes.IndexOf("AmbientUsageRailMenu"u8) >= 0 && bytes.IndexOf("fiveHourRemainingTooltip"u8) >= 0;
            var domCandidate = bytes.IndexOf("react-dom-client"u8) >= 0 && bytes.IndexOf("createRoot"u8) >= 0;
            if (!runtimeCandidate && !usageCandidate && !domCandidate) continue;
            if (!MatchesIntegrity(entry, bytes)) return (State.CorruptSource, null, null, null);

            var source = SourceEncoding.GetString(bytes);
            if (runtimeCandidate && CodexDesktopNativeUsageRecipe.ExportsRuntime(source))
            {
                if (runtime is not null || entries[relativePath].Skip(1).Any()) return (State.AmbiguousContract, null, null, null);
                runtime = relativePath;
            }

            if (usageCandidate && CodexDesktopNativeUsageRecipe.FindUsageModule(source) is { } foundUsage)
            {
                if (usage is not null) return (State.AmbiguousContract, null, null, null);
                usage = foundUsage;
            }

            if (domCandidate && CodexDesktopNativeUsageRecipe.FindReactDomModule(source) is { } foundDom)
            {
                if (dom is not null) return (State.AmbiguousContract, null, null, null);
                dom = foundDom;
            }
        }

        return runtime is not null && usage is not null && dom is not null
            ? (State.Ready, runtime, usage, dom)
            : (State.UnsupportedContract, null, null, null);
    }

    private static async Task<ArchiveIndex?> ReadIndexAsync(Stream archive, CancellationToken cancellationToken)
    {
        var prefix = new byte[8];
        await archive.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4));
        if (BinaryPrimitives.ReadInt32LittleEndian(prefix) != 4 || headerSize is < 8 or > 8 * 1024 * 1024 ||
            headerSize > archive.Length - prefix.Length)
        {
            return null;
        }

        var header = new byte[headerSize];
        await archive.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (jsonLength <= 0 || jsonLength > header.Length - 8) return null;

        var json = header.AsMemory(8, jsonLength);
        return new(prefix.Length + header.Length, JsonDocument.Parse(json), Convert.ToHexString(SHA256.HashData(json.Span)));
    }

    private static bool MatchesIntegrity(JsonElement entry, ReadOnlySpan<byte> bytes) =>
        entry.TryGetProperty("integrity", out var integrity) && integrity.TryGetProperty("algorithm", out var algorithm) &&
        algorithm.ValueEquals("SHA256") && integrity.TryGetProperty("hash", out var hash) && hash.GetString() is { Length: 64 } expected &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expected));

    private static bool IsCorruptArchive(Exception exception) => exception is EndOfStreamException or JsonException or
        InvalidOperationException or DecoderFallbackException or FormatException or OverflowException;

    private static bool TryGetFiles(JsonElement node, out JsonElement files) =>
        node.TryGetProperty("files", out files) && files.ValueKind == JsonValueKind.Object;

    private static Asset? ReadAssetLocation(JsonElement entry, long dataStart, long archiveLength) =>
        TryReadAsset(entry, dataStart, archiveLength, out var asset) ? asset : null;

    private static bool TryReadAsset(JsonElement entry, long dataStart, long archiveLength, out Asset asset)
    {
        asset = default;
        if (entry.TryGetProperty("unpacked", out _) || !entry.TryGetProperty("size", out var sizeProperty) ||
            !sizeProperty.TryGetInt32(out var size) || size is <= 0 or > MaximumAssetBytes ||
            !entry.TryGetProperty("offset", out var offsetProperty) ||
            !long.TryParse(offsetProperty.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
            offset > archiveLength - dataStart - size)
        {
            return false;
        }

        asset = new(dataStart + offset, size);
        return true;
    }

    private static ValueTask ReadAssetAsync(Stream archive, Asset asset, byte[] buffer, CancellationToken cancellationToken)
    {
        if (archive.Position != asset.Offset) archive.Position = asset.Offset;
        return archive.ReadExactlyAsync(buffer.AsMemory(0, asset.Size), cancellationToken);
    }

    private static IEnumerable<(string Path, JsonElement Entry)> EnumerateAssets(JsonElement files, string prefix, int depth)
    {
        foreach (var entry in files.EnumerateObject())
        {
            var name = entry.Name;
            if (name is "." or ".." || !AssetName().IsMatch(name)) continue;

            if (TryGetFiles(entry.Value, out var children))
            {
                if (depth >= 3) continue;
                foreach (var child in EnumerateAssets(children, prefix + name + "/", depth + 1)) yield return child;
            }
            else
            {
                yield return (prefix + name, entry.Value);
            }
        }
    }

    [GeneratedRegex("""<script\b(?=[^>]*\stype\s*=\s*["']module["'])[^>]*\ssrc\s*=\s*["'](?<src>/assets/(?:[a-zA-Z0-9._-]+/)*[a-zA-Z0-9._-]+\.js)["'][^>]*>""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModuleScript();

    [GeneratedRegex(@"^[a-zA-Z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetName();

    private readonly record struct ArchiveIdentity(string Path, Version Version, Version Recipe, long Length, DateTime LastWriteUtc, string IndexSha256);

    private readonly record struct Asset(long Offset, int Size);

    private sealed record ArchiveIndex(long DataStart, JsonDocument Json, string Sha256) : IDisposable
    {
        public void Dispose() => Json.Dispose();
    }
}
