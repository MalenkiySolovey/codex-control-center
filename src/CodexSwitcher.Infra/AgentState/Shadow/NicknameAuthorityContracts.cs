using System.Text.Json.Serialization;

namespace CodexSwitcher.Infra.AgentState.Shadow;

internal sealed record AccountSourceDescriptor
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("accountId")] public required string AccountId { get; init; }
    [JsonPropertyName("safeFallbackDisplayName")] public required string SafeFallbackDisplayName { get; init; }
    [JsonPropertyName("health")] public required string Health { get; init; }
    [JsonPropertyName("isActive")] public bool IsActive { get; init; }
    [JsonPropertyName("sortOrder")] public int SortOrder { get; init; }
    [JsonPropertyName("createdAt")] public required string CreatedAt { get; init; }
    [JsonPropertyName("planType")] public string? PlanType { get; init; }
}

internal sealed record AccountSourceCollection
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("accounts")] public required IReadOnlyList<AccountSourceDescriptor> Accounts { get; init; }
}

internal sealed record AccountNicknameState
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("accountId")] public required string AccountId { get; init; }
    [JsonPropertyName("nickname")] public required string? Nickname { get; init; }
    [JsonPropertyName("revision")] public required string Revision { get; init; }
}

internal sealed record AccountNicknameCollection
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("accounts")] public required IReadOnlyList<AccountNicknameState> Accounts { get; init; }
}

internal sealed record AccountNicknameMutation
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("accountId")] public required string AccountId { get; init; }
    [JsonPropertyName("nickname")] public required string? Nickname { get; init; }
    [JsonPropertyName("safeFallbackDisplayName")] public required string SafeFallbackDisplayName { get; init; }
    [JsonPropertyName("expectedRevision")] public required string ExpectedRevision { get; init; }
}

internal sealed record NicknameAuthorityStatus
{
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("epoch")] public long Epoch { get; init; }
    [JsonPropertyName("protocolManifestSha256")] public required string ProtocolManifestSha256 { get; init; }
    [JsonPropertyName("migrationId")] public required string? MigrationId { get; init; }
}

internal sealed record NicknameMutationResult
{
    [JsonPropertyName("nicknameState")] public required AccountNicknameState NicknameState { get; init; }
    [JsonPropertyName("account")] public required CodexSwitcher.Core.AgentState.V1.AccountDescriptor Account { get; init; }
}

internal sealed record SourceReconcileResult
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("inserted")] public int Inserted { get; init; }
    [JsonPropertyName("updated")] public int Updated { get; init; }
    [JsonPropertyName("deleted")] public int Deleted { get; init; }
    [JsonPropertyName("unchanged")] public int Unchanged { get; init; }
}

internal sealed record AuthorityDaemonInfo(
    int DatabaseSchemaVersion,
    string AuthorityState,
    bool ExperimentalNicknameAuthorityEnabled,
    IReadOnlyDictionary<string, bool> CapabilityEnabled);

internal static class NicknameAuthorityStates
{
    internal const string LegacyLocal = "LegacyLocal";
    internal const string Preparing = "Preparing";
    internal const string Verified = "Verified";
    internal const string DaemonNicknameAuthority = "DaemonNicknameAuthority";
    internal const string RollbackRequired = "RollbackRequired";

    internal static bool IsKnown(string? state) => state is LegacyLocal or Preparing or Verified or DaemonNicknameAuthority or RollbackRequired;
    internal static bool IsAuthorityPossible(string? state) => state is Preparing or Verified or DaemonNicknameAuthority or RollbackRequired;
}
