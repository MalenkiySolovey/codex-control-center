using System.Globalization;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Enums;

namespace CodexSwitcher.Core.AgentState.V1;

/// <summary>Portable account metadata, intentionally distinct from the profile persistence model.</summary>
public sealed record AccountDescriptor(
    int SchemaVersion,
    string AccountId,
    string DisplayName,
    string Health,
    bool IsActive,
    int SortOrder,
    string CreatedAt,
    string? PlanType);

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Matches the language-neutral AccountCollection v1 contract name.")]
public sealed record AccountCollection(
    int SchemaVersion,
    IReadOnlyList<AccountDescriptor> Accounts);

/// <summary>A rename request. expectedRevision is opaque and is optional until the owner supports CAS.</summary>
public sealed record AccountMetadataUpdate(
    int SchemaVersion,
    string AccountId,
    string Nickname,
    string? ExpectedRevision = null);

/// <summary>A descriptor paired with an opaque owner-issued CAS revision.</summary>
public sealed record RevisionedAccountDescriptor(AccountDescriptor Account, string Revision);

/// <summary>Reference to secret material held by a separate protected store; it never contains secret bytes.</summary>
public sealed record SecretReference(
    int SchemaVersion,
    string SecretId,
    string Kind);

public sealed record CapabilityDescriptor(
    int SchemaVersion,
    string CapabilityId,
    bool Supported,
    IReadOnlyDictionary<string, object?>? Extensions = null);

public sealed record CredentialDescriptor(
    int SchemaVersion,
    string CredentialId,
    string OwnerId,
    string Kind,
    string State,
    string? Revision = null,
    string? UpdatedAt = null);

public sealed record ProviderDescriptor(
    int SchemaVersion,
    string ProviderId,
    string DisplayName,
    string? BaseEndpoint,
    IReadOnlyList<CapabilityDescriptor> Capabilities,
    SecretReference? Credential = null);

public sealed record UsageMeasurement(
    string Name,
    double? Used,
    double? Limit,
    double? Remaining,
    string Unit,
    string? ResetAt);

public sealed record UsageSnapshot(
    int SchemaVersion,
    string SubjectId,
    string SubjectKind,
    string? ObservedAt,
    string Source,
    string Freshness,
    string State,
    IReadOnlyList<UsageMeasurement> Measurements);

/// <summary>Sanitized, machine-readable application error. Protocol error numbers remain transport-owned.</summary>
public sealed record ErrorDetail(
    int SchemaVersion,
    string ErrorCode,
    string Message,
    IReadOnlyDictionary<string, string>? Details = null,
    bool? Retryable = null);

public static partial class ErrorDetailFactory
{
    private static readonly Regex SensitiveDetailName = new(
        "(?i)(token|secret|password|api.?key|auth.?json|credential)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [GeneratedRegex("(?i)\\bBearer\\s+[^\\s\\\"',;]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex("(?i)\\b(?:access_token|refresh_token|id_token|api_key|totp_secret)\\s*([=:])\\s*[^&\\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecret();

    [GeneratedRegex("\\bsk-[A-Za-z0-9_-]{8,}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKey();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{4,}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    public static ErrorDetail FromMessage(
        string errorCode,
        string message,
        IReadOnlyDictionary<string, string>? details = null,
        bool? retryable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var safeDetails = details is null
            ? null
            : new SortedDictionary<string, string>(
                details.Where(entry => !SensitiveDetailName.IsMatch(entry.Key))
                    .ToDictionary(entry => entry.Key, entry => Redact(entry.Value), StringComparer.Ordinal),
                StringComparer.Ordinal);

        return new ErrorDetail(
            SchemaVersion: 1,
            ErrorCode: errorCode,
            Message: Redact(message),
            Details: safeDetails,
            Retryable: retryable);
    }

    private static string Redact(string value)
    {
        var sanitized = BearerToken().Replace(value, "Bearer [REDACTED]");
        sanitized = NamedSecret().Replace(sanitized, "$1[REDACTED]");
        sanitized = ApiKey().Replace(sanitized, "[REDACTED]");
        return Jwt().Replace(sanitized, "[REDACTED]");
    }
}

/// <summary>Maps the real local profile model to the intentionally redacted read-only account projection.</summary>
public static class AccountDescriptorProjector
{
    public static AccountDescriptor FromProfile(ProfileMetadata profile, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (ordinal < 1)
            throw new ArgumentOutOfRangeException(nameof(ordinal), "Account ordinals are one-based.");

        return new AccountDescriptor(
            SchemaVersion: 1,
            AccountId: profile.Id.ToString("D", CultureInfo.InvariantCulture),
            DisplayName: GetSafeDisplayName(profile, ordinal),
            Health: profile.HealthStatus switch
            {
                HealthStatus.Valid => "valid",
                HealthStatus.NeedsReLogin => "needs_relogin",
                _ => "unknown",
            },
            IsActive: profile.IsActive,
            SortOrder: profile.SortOrder,
            CreatedAt: FormatUtc(profile.CreatedAt),
            PlanType: profile.PlanType);
    }

    private static string GetSafeDisplayName(ProfileMetadata profile, int ordinal)
    {
        if (!string.IsNullOrWhiteSpace(profile.Nickname) && !LooksLikeEmail(profile.Nickname))
            return profile.Nickname.Trim();

        if (!string.IsNullOrWhiteSpace(profile.DetectedDisplayName) && !LooksLikeEmail(profile.DetectedDisplayName))
            return profile.DetectedDisplayName.Trim();

        return $"ChatGPT account {ordinal}";
    }

    private static bool LooksLikeEmail(string value) => value.Contains('@');

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
