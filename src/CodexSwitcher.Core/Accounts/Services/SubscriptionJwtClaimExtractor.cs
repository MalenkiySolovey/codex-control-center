using ChatGpt.Accounts;
using CodexSwitcher.Core.Accounts.Models;

namespace CodexSwitcher.Core.Accounts.Services;

/// <summary>Maps account-kit subscription observations to the persisted Control Center schema.</summary>
public static class SubscriptionJwtClaimExtractor
{
    public static DetectedSubscriptionInfo? Extract(byte[]? authJsonBytes, DateTimeOffset now)
    {
        if (authJsonBytes is null || authJsonBytes.Length == 0)
            return null;
        return Map(SubscriptionMetadataExtractor.Extract(authJsonBytes, now));
    }

    public static DetectedSubscriptionInfo? ExtractFromJwt(string? jwtToken, DateTimeOffset now) =>
        Map(SubscriptionMetadataExtractor.ExtractFromJwt(jwtToken, now));

    private static DetectedSubscriptionInfo? Map(ChatGpt.Accounts.Models.DetectedSubscriptionMetadata? detected) =>
        detected is null
            ? null
            : new DetectedSubscriptionInfo(
                detected.ActiveStartUtc,
                detected.ActiveUntilUtc,
                detected.ObservedAtUtc,
                detected.PlanType,
                detected.Source switch
                {
                    ChatGpt.Accounts.Models.SubscriptionMetadataSource.EstimatedFromStart => DetectedSubscriptionSource.EstimatedFromStart,
                    _ => DetectedSubscriptionSource.OAuthTokenClaim,
                },
                detected.IsStale);
}
