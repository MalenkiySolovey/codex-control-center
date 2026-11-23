using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Codex.RuntimeKit.Desktop.Discovery;
using Codex.RuntimeKit.Desktop.Extensions;

namespace CodexSwitcher.App.Features.CodexDesktopBalance;

/// <summary>Loads the native usage component recipe for one exact installed Codex Desktop build.</summary>
internal static class CodexDesktopNativeUsagePackage
{
    internal const string Id = "codex-control-center-api-balance-native";
    internal const string RootAttribute = "data-codex-control-center-api-balance-native";
    internal const string ApprovedSha256 = "A36C096404BEF32A4519F1BA8E5E8A2DD10E3FF3B6C2175F45CC0B070BF438E0";
    private const string SourceArchiveSha256 = "8A437AAD94098770C1BF5FD5385C204E162B62C6D7917F688741D74E05BABE90";
    private const string SourceChunkSha256 = "B0606AD7D47549336938132BED9AB10FF636C73E01CBC4FB0E69BC3AE420F698";
    private const long SourceArchiveLength = 443_800_006;
    private const string SourceChunkPath = "webview/assets/async/212106.25764f7d75.js";
    private const string FactoryStart = "w5W4(e,n,i){";
    private const string FactoryEnd = "},mBEO(e,n,i){";
    private const string SourceExport = "i.d(n,{AmbientUsageRailMenu:()=>K})";
    private const string NativeExports = "i.d(n,{Ring:()=>W,Glyph:()=>R,Popup:()=>u.Ab,Menu:()=>u.ms,Row:()=>G,Trigger:()=>g.l,Tooltip:()=>l.m})";
    private const string ArcTooltipAnchor = "let E=B&&null!=P;";
    private const string ArcTooltipAdapter =
        "if(\"primary\"===P&&typeof e.primaryTooltip===\"string\")r=e.primaryTooltip;else if(\"secondary\"===P&&typeof e.secondaryTooltip===\"string\")r=e.secondaryTooltip;let E=B&&null!=P;";
    private const string TemplateSlot = "/* CODEX_NATIVE_USAGE_FACTORY */ null";
    private const string ResourceName = "CodexSwitcher.App.Features.CodexDesktopBalance.CodexDesktopNativeUsage.js";
    private static readonly Version DesktopVersion = new(26, 1117, 4957, 0);
    private static readonly Version RecipeVersion = new(1, 1, 0);

    internal static bool SupportsInstallation(CodexDesktopInstallation installation) =>
        installation.Version == DesktopVersion && installation.PackageName == "OpenAI.Codex" &&
        installation.PackageFamilyName == "OpenAI.Codex_2p2nqsd0c76g0";

    public static async Task<CodexDesktopExtensionPackage?> TryLoadAsync(
        CodexDesktopInstallation installation, CancellationToken cancellationToken)
    {
        if (!SupportsInstallation(installation))
        {
            return null;
        }

        try
        {
            var archivePath = Path.Combine(Path.GetDirectoryName(installation.ExecutablePath)!, "resources", "app.asar");
            await using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (archive.Length != SourceArchiveLength ||
                !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(archive, cancellationToken).ConfigureAwait(false),
                    Convert.FromHexString(SourceArchiveSha256)))
            {
                return null;
            }

            archive.Position = 0;
            var bytes = await ReadSourceChunkAsync(archive, cancellationToken).ConfigureAwait(false);
            if (bytes is null || !MatchesHash(bytes, SourceChunkSha256))
            {
                return null;
            }

            using var resource = typeof(CodexDesktopNativeUsagePackage).Assembly.GetManifestResourceStream(ResourceName);
            if (resource is null)
            {
                return null;
            }

            using var reader = new StreamReader(resource, new UTF8Encoding(false, true));
            var template = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false))
                .ReplaceLineEndings("\n");
            var script = TryBuildScript(new UTF8Encoding(false, true).GetString(bytes), template);
            if (script is null || !MatchesHash(Encoding.UTF8.GetBytes(script), ApprovedSha256))
            {
                return null;
            }

            return new(Id, RecipeVersion, DesktopVersion, DesktopVersion, ApprovedSha256, script)
            {
                RestorePayloadOnReload = false,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException or DecoderFallbackException)
        {
            return null;
        }
    }

    internal static string? TryBuildScript(string source, string template)
    {
        var start = FindUnique(source, FactoryStart);
        var end = FindUnique(source, FactoryEnd);
        if (start < 0 || end <= start || FindUnique(template, TemplateSlot) < 0)
        {
            return null;
        }

        var factory = source[start..(end + 1)];
        var meter = FindUnique(factory, "function W(e)");
        var tooltip = FindUnique(factory, ArcTooltipAnchor);
        if (FindUnique(factory, SourceExport) != FactoryStart.Length ||
            FindUnique(factory, "contentWidth:\"menuReadout\"") < 0 ||
            FindUnique(factory, "function R(e)") < 0 ||
            FindUnique(factory, "function G(e,n)") < 0 ||
            meter < 0 || tooltip <= meter || tooltip >= factory.IndexOf("function ", meter + 1, StringComparison.Ordinal))
        {
            return null;
        }

        // The usage component retains its native hit testing, animation and tooltip rendering.
        // This separate module instance supplies the API text for each segment.
        var adapted = factory.Replace(SourceExport, NativeExports, StringComparison.Ordinal)
            .Replace(ArcTooltipAnchor, ArcTooltipAdapter, StringComparison.Ordinal);
        return template.Replace(TemplateSlot, "function " + adapted, StringComparison.Ordinal);
    }

    private static async Task<byte[]?> ReadSourceChunkAsync(Stream archive, CancellationToken cancellationToken)
    {
        var prefix = new byte[8];
        await archive.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4));
        if (BinaryPrimitives.ReadInt32LittleEndian(prefix) != 4 || headerSize is < 8 or > 8 * 1024 * 1024)
        {
            return null;
        }

        var header = new byte[headerSize];
        await archive.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (jsonLength <= 0 || jsonLength > header.Length - 8)
        {
            return null;
        }

        using var json = JsonDocument.Parse(header.AsMemory(8, jsonLength));
        var entry = json.RootElement;
        foreach (var part in SourceChunkPath.Split('/'))
        {
            entry = entry.GetProperty("files").GetProperty(part);
        }

        var size = entry.GetProperty("size").GetInt32();
        var dataStart = prefix.Length + header.Length;
        if (entry.TryGetProperty("unpacked", out _) || size is <= 0 or > 128 * 1024 ||
            !long.TryParse(entry.GetProperty("offset").GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
            offset > archive.Length - dataStart - size)
        {
            return null;
        }

        archive.Position = dataStart + offset;
        var bytes = new byte[size];
        await archive.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static int FindUnique(string source, string marker)
    {
        var first = source.IndexOf(marker, StringComparison.Ordinal);
        return first < 0 || source.IndexOf(marker, first + marker.Length, StringComparison.Ordinal) >= 0 ? -1 : first;
    }

    private static bool MatchesHash(byte[] bytes, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expected));
}
