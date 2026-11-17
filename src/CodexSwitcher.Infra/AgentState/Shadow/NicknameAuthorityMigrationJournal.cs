using System.Text.Json;
using System.Text.Json.Serialization;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Security.Hardening;

namespace CodexSwitcher.Infra.AgentState.Shadow;

internal sealed record NicknameAuthorityMigrationRow
{
    [JsonPropertyName("accountId")] public required string AccountId { get; init; }
    [JsonPropertyName("daemonRevision")] public string? DaemonRevision { get; init; }
    [JsonPropertyName("phase")] public required string Phase { get; init; }
}

internal sealed record NicknameAuthorityJournal
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; init; } = 1;
    [JsonPropertyName("protocolManifestSha256")] public string ProtocolManifestSha256 { get; init; } = AccountStateShadowRpcClient.ProtocolManifestSha256;
    [JsonPropertyName("migrationId")] public string? MigrationId { get; init; }
    [JsonPropertyName("authorityState")] public required string AuthorityState { get; init; }
    [JsonPropertyName("authorityEpoch")] public long AuthorityEpoch { get; init; }
    [JsonPropertyName("sourceGeneration")] public long SourceGeneration { get; init; }
    [JsonPropertyName("authorityWasActivated")] public bool AuthorityWasActivated { get; init; }
    [JsonPropertyName("rollbackCompleted")] public bool RollbackCompleted { get; init; }
    [JsonPropertyName("rows")] public IReadOnlyList<NicknameAuthorityMigrationRow> Rows { get; init; } = [];
}

internal sealed class NicknameAuthorityMigrationJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly IFileSystem _fileSystem;
    private readonly string _directory;
    private readonly string _path;

    internal NicknameAuthorityMigrationJournal(IFileSystem fileSystem, AppPaths paths)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        ArgumentNullException.ThrowIfNull(paths);
        _directory = Path.Combine(paths.Root, "agent-state-shadow-v1");
        _path = Path.Combine(_directory, "nickname-authority-migration-v1.json");
    }

    internal bool Exists => _fileSystem.FileExists(_path);

    internal void Delete() => _fileSystem.Delete(_path);

    internal NicknameAuthorityJournal? Read()
    {
        if (!_fileSystem.FileExists(_path))
            return null;

        var info = new FileInfo(_path);
        if (info.Length > 1_048_576)
            throw new InvalidDataException("The nickname authority journal exceeds its supported size.");

        var journal = JsonSerializer.Deserialize<NicknameAuthorityJournal>(_fileSystem.ReadAllText(_path), JsonOptions)
            ?? throw new InvalidDataException("The nickname authority journal is invalid.");
        Validate(journal);
        return journal;
    }

    internal void Save(NicknameAuthorityJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Validate(journal);
        if (!DirectoryHardening.TryRestrictToCurrentUser(_directory))
            throw new UnauthorizedAccessException("The nickname authority journal directory is not private.");

        var json = JsonSerializer.Serialize(journal, JsonOptions);
        if (json.Length > 1_048_576)
            throw new InvalidDataException("The nickname authority journal exceeds its supported size.");
        _fileSystem.WriteAllTextAtomic(_path, json);
    }

    private static void Validate(NicknameAuthorityJournal journal)
    {
        if (journal.FormatVersion != 1 ||
            journal.ProtocolManifestSha256 != AccountStateShadowRpcClient.ProtocolManifestSha256 ||
            !NicknameAuthorityStates.IsKnown(journal.AuthorityState) || journal.AuthorityEpoch < 0 ||
            journal.SourceGeneration < 0 || journal.Rows is null || journal.Rows.Count > 1_024 ||
            journal.MigrationId is { Length: > 256 } ||
            (journal.MigrationId is not null && string.IsNullOrWhiteSpace(journal.MigrationId)))
        {
            throw new InvalidDataException("The nickname authority journal is incompatible.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in journal.Rows)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.AccountId) || row.AccountId.Length > 256 ||
                row.DaemonRevision is { Length: > 256 } || row.Phase.Length is < 1 or > 64 ||
                !ids.Add(row.AccountId))
            {
                throw new InvalidDataException("The nickname authority journal contains an invalid row.");
            }
        }
    }
}
