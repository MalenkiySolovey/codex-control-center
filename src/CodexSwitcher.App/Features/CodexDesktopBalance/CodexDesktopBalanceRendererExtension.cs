using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace CodexSwitcher.App.Features.CodexDesktopBalance;

/// <summary>
/// Identifies the bundled renderer resource and its reviewed content hash.
/// Build compatibility is intentionally decided separately from this asset.
/// </summary>
public static class CodexDesktopBalanceRendererExtension
{
    public const string Id = "codex-control-center-api-balance";
    public const string PackageVersion = "0.1.1";
    public const string ApprovedSha256 = "0940F0BC71FD14FE89A732B0D802CE17340FC71EFC8B59DED3FD6D512457367D";
    private const string ResourceName = "CodexSwitcher.App.Features.CodexDesktopBalance.CodexDesktopBalanceRendererExtension.js";

    public static string LoadJavaScript()
    {
        using var resource = typeof(CodexDesktopBalanceRendererExtension).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The bundled Codex Desktop balance renderer resource is missing.");
        using var reader = new StreamReader(resource, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    public static bool MatchesApprovedHash(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(script));
        var expected = Convert.FromHexString(ApprovedSha256);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
