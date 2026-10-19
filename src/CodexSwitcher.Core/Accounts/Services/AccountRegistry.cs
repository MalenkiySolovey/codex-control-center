using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Time;

namespace CodexSwitcher.Core.Accounts.Services;

/// <summary>
/// Owns the in-memory account metadata registry and its persistence lifecycle.
/// Credential bytes, Codex runtime state, routing, and UI concerns remain outside this service.
/// </summary>
public sealed class AccountRegistry
{
    private readonly IProfileStore _store;
    private readonly IClock _clock;
    private readonly IAuditLog _audit;
    private List<ProfileMetadata> _profiles = [];

    internal object SyncRoot { get; } = new();

    public IReadOnlyList<ProfileMetadata> Profiles => _profiles.AsReadOnly();

    internal List<ProfileMetadata> MutableProfiles => _profiles;

    public AccountRegistry(IProfileStore store, IClock clock, IAuditLog audit)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public IReadOnlyList<ProfileMetadata> Load()
    {
        lock (SyncRoot)
        {
            LoadUnderLock();
            return Profiles;
        }
    }

    public ProfileMetadata? Find(Guid id)
    {
        lock (SyncRoot)
            return FindUnderLock(id);
    }

    public ProfileMetadata? FindByAccountSub(string? accountSub)
    {
        lock (SyncRoot)
            return FindByAccountSubUnderLock(accountSub);
    }

    public void Rename(Guid id, string nickname)
    {
        lock (SyncRoot)
        {
            var profile = FindUnderLock(id);
            if (profile is null)
                return;

            profile.Nickname = nickname?.Trim() ?? string.Empty;
            SaveUnderLock();
            _audit.Record("rename", "ok", profile.DisplayName);
        }
    }

    public void UpdateSubscriptionTracking(Guid id, SubscriptionTracking? tracking)
    {
        lock (SyncRoot)
        {
            var profile = FindUnderLock(id);
            if (profile is null)
                return;

            profile.SubscriptionTracking = tracking;
            SaveUnderLock();
            _audit.Record("subscription-tracking", "update", profile.DisplayName);
        }
    }

    public void MarkNeedsReLogin(Guid id)
    {
        lock (SyncRoot)
        {
            var profile = FindUnderLock(id);
            if (profile is null)
                return;

            profile.HealthStatus = HealthStatus.NeedsReLogin;
            profile.LastError = ErrorInfo.Create(
                ErrorCategory.RefreshTokenExpired,
                "Marcado manualmente como precisa re-login.",
                _clock.UtcNow);
            SaveUnderLock();
        }
    }

    public void MarkUsed(Guid id)
    {
        lock (SyncRoot)
        {
            var profile = FindUnderLock(id);
            if (profile is null)
                return;

            profile.MarkedUsedAt = _clock.UtcNow;
            SaveUnderLock();
            _audit.Record("mark-used", "ok", profile.DisplayName);
        }
    }

    public void UnmarkUsed(Guid id)
    {
        lock (SyncRoot)
        {
            var profile = FindUnderLock(id);
            if (profile is null)
                return;

            profile.MarkedUsedAt = null;
            SaveUnderLock();
            _audit.Record("unmark-used", "ok", profile.DisplayName);
        }
    }

    /// <summary>
    /// Reorders metadata by the supplied IDs. Unknown IDs are ignored to preserve callers that
    /// submit a filtered UI view; only the persisted sort order is changed.
    /// </summary>
    public void Reorder(IReadOnlyList<Guid> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        lock (SyncRoot)
        {
            var order = 0;
            foreach (var id in orderedIds)
            {
                var profile = FindUnderLock(id);
                if (profile is null)
                    continue;

                profile.SortOrder = order++;
            }

            SaveUnderLock();
            _audit.Record("reorder", "ok");
        }
    }

    public void Save()
    {
        lock (SyncRoot)
            SaveUnderLock();
    }

    internal ProfileMetadata? FindUnderLock(Guid id) => _profiles.FirstOrDefault(profile => profile.Id == id);

    internal ProfileMetadata? FindByAccountSubUnderLock(string? accountSub) =>
        string.IsNullOrEmpty(accountSub)
            ? null
            : _profiles.FirstOrDefault(profile => profile.AccountSub == accountSub);

    internal void LoadUnderLock()
    {
        _profiles = _store.LoadAll();
        EnsureSortOrderInitializedUnderLock();
    }

    internal void AddUnderLock(ProfileMetadata profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_profiles.Any(existing => existing.Id == profile.Id))
            throw new InvalidOperationException($"Account ID '{profile.Id}' is already registered.");

        _profiles.Add(profile);
        SaveUnderLock();
    }

    internal void RemoveUnderLock(ProfileMetadata profile, ProfileSaveIntent intent)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!_profiles.Remove(profile))
            return;

        SaveUnderLock(intent);
    }

    internal void SaveUnderLock(ProfileSaveIntent intent = ProfileSaveIntent.NormalUpdate) =>
        _store.SaveAll(_profiles, intent);

    private void EnsureSortOrderInitializedUnderLock()
    {
        if (_profiles.Count < 2 || _profiles.Select(profile => profile.SortOrder).Distinct().Count() > 1)
            return;

        var legacyOrder = _profiles
            .OrderByDescending(profile => profile.IsActive)
            .ThenByDescending(profile => profile.LastSwitchedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(profile => profile.CreatedAt)
            .ToList();

        for (var index = 0; index < legacyOrder.Count; index++)
            legacyOrder[index].SortOrder = index;

        SaveUnderLock();
    }
}
