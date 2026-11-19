using System.Text.Json;
using ChatGpt.Accounts;
using CodexSwitcher.Core.Accounts.Models;

namespace CodexSwitcher.Core.Security.Secrets;

/// <summary>
/// Control Center adapter for the Codex auth.json envelope. Account metadata validation and
/// interpretation belong to ChatGpt.Accounts; this adapter retains only the short-lived ID token
/// needed by existing Codex-specific callers.
/// </summary>
public static class AuthJsonReader
{
    public static AuthFileInfo? TryRead(byte[] authJsonBytes)
    {
        ArgumentNullException.ThrowIfNull(authJsonBytes);
        var inspection = CredentialInspector.Inspect(authJsonBytes, DateTimeOffset.UtcNow);
        if (inspection.Status is ChatGpt.Accounts.Models.CredentialDocumentStatus.InvalidJson or
            ChatGpt.Accounts.Models.CredentialDocumentStatus.InvalidShape)
            return null;

        string? idToken = null;
        try
        {
            using var document = JsonDocument.Parse(authJsonBytes);
            if (document.RootElement.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object &&
                tokens.TryGetProperty("id_token", out var tokenValue) && tokenValue.ValueKind == JsonValueKind.String)
            {
                idToken = tokenValue.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return new AuthFileInfo(inspection.AuthMode, inspection.LastRefresh, idToken, inspection.AccountId);
    }

    public static (AuthFileInfo? File, AccountClaims Claims) Identify(byte[] authJsonBytes)
    {
        var info = TryRead(authJsonBytes);
        return (info, JwtClaimsReader.Read(info?.IdToken));
    }
}
