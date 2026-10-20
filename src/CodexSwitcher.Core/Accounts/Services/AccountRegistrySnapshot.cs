using CodexSwitcher.Core.Accounts.Models;

namespace CodexSwitcher.Core.Accounts.Services;

/// <summary>A committed registry view captured atomically with its monotonic generation.</summary>
public sealed record AccountRegistrySnapshot(long Generation, IReadOnlyList<ProfileMetadata> Profiles);
