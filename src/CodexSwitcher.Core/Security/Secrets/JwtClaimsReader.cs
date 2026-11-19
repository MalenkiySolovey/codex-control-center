using ChatGpt.Accounts;
using CodexSwitcher.Core.Accounts.Models;

namespace CodexSwitcher.Core.Security.Secrets;

/// <summary>Compatibility adapter from ChatGpt.Accounts claims to Control Center account models.</summary>
public static class JwtClaimsReader
{
    public static AccountClaims Read(string? idToken)
    {
        var claims = CredentialInspector.ReadClaimsFromToken(idToken);
        return new AccountClaims(claims.Subject, claims.Email, claims.TokenExpiresAt, claims.PlanType, claims.DisplayName);
    }
}
