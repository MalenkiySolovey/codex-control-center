using ChatGpt.Accounts;

namespace CodexSwitcher.Core.Security.Secrets;

/// <summary>Compatibility adapter for the account kit's stable credential fingerprint.</summary>
public static class Fingerprint
{
    public static string Compute(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return CredentialFingerprint.Compute(content);
    }
}
