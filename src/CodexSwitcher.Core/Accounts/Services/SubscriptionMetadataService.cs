using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Security.Secrets;

namespace CodexSwitcher.Core.Accounts.Services;

/// <summary>
/// Extracts and applies local, auth-derived display and subscription metadata.
/// This service performs no I/O and never treats a token expiry as a subscription date.
/// </summary>
public static class SubscriptionMetadataService
{
    /// <summary>
    /// Fills only missing detected metadata. A token belonging to a different known account is
    /// ignored so a stale active slot cannot enrich the wrong registry entry.
    /// </summary>
    public static bool EnrichMissing(ProfileMetadata profile, byte[] authJson, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(authJson);

        var (_, claims) = AuthJsonReader.Identify(authJson);
        if (!MatchesKnownAccountSub(profile, claims.Sub))
            return false;

        var changed = false;
        if (string.IsNullOrWhiteSpace(profile.DetectedDisplayName) &&
            !string.IsNullOrWhiteSpace(claims.DisplayName))
        {
            profile.DetectedDisplayName = claims.DisplayName;
            changed = true;
        }

        if (profile.DetectedSubscription is null &&
            SubscriptionJwtClaimExtractor.Extract(authJson, now) is { } detected)
        {
            profile.DetectedSubscription = detected;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Applies the latest auth-derived display/subscription values after a credential add,
    /// refresh, or reconciliation. Manual subscription tracking is intentionally untouched.
    /// </summary>
    public static void ApplyCurrentCredential(ProfileMetadata profile, byte[] authJson, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(authJson);

        var (_, claims) = AuthJsonReader.Identify(authJson);
        if (!MatchesKnownAccountSub(profile, claims.Sub))
            return;

        if (!string.IsNullOrWhiteSpace(claims.DisplayName))
            profile.DetectedDisplayName = claims.DisplayName;

        if (SubscriptionJwtClaimExtractor.Extract(authJson, now) is { } detected)
            profile.DetectedSubscription = detected;
    }

    private static bool MatchesKnownAccountSub(ProfileMetadata profile, string? accountSub) =>
        string.IsNullOrWhiteSpace(profile.AccountSub) ||
        string.Equals(profile.AccountSub, accountSub, StringComparison.Ordinal);
}
