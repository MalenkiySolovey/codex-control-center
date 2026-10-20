using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Time;

namespace CodexSwitcher.Core.Accounts.Services;

/// <summary>
/// Owns the in-memory account metadata registry and its persistence lifecycle.
/// Reads are detached snapshots; changes are applied and persisted here. Credential bytes,
/// Codex runtime state, routing, and UI concerns remain outside this service.
/// </summary>
public sealed class AccountRegistry
{
    private readonly IProfileStore _store;
    private readonly IClock _clock;
    private readonly IAuditLog _audit;
    private List<ProfileMetadata> _profiles = [];
    private bool _isLoaded;
    private bool _isLoading;
    private long _currentGeneration;

    internal object SyncRoot { get; } = new();

    /// <summary>
    /// Raised after the registry has loaded or successfully persisted a new snapshot.
    /// Subscribers should signal background work and avoid blocking the registry operation.
    /// </summary>
    public event Action? SnapshotChanged;

    public bool IsLoaded
    {
        get
        {
            lock (SyncRoot)
                return _isLoaded;
        }
    }

    /// <summary>Returns a defensive snapshot; mutating its profiles cannot alter registry state.</summary>
    public IReadOnlyList<ProfileMetadata> Profiles => Snapshot();

    public AccountRegistry(IProfileStore store, IClock clock, IAuditLog audit)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public IReadOnlyList<ProfileMetadata> Snapshot()
    {
        lock (SyncRoot)
            return SnapshotUnderLock();
    }

    /// <summary>
    /// The version of the last successfully loaded or persisted registry state.
    /// Failed persistence never advances this value.
    /// </summary>
    public long CurrentGeneration
    {
        get
        {
            lock (SyncRoot)
                return _currentGeneration;
        }
    }

    /// <summary>Captures detached profile metadata and its committed generation under one lock.</summary>
    public AccountRegistrySnapshot ReadCommittedSnapshot()
    {
        lock (SyncRoot)
            return new AccountRegistrySnapshot(_currentGeneration, SnapshotUnderLock());
    }

    public IReadOnlyList<ProfileMetadata> Load()
    {
        lock (SyncRoot)
        {
            LoadUnderLock();
            return SnapshotUnderLock();
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

    /// <summary>Adds already-validated non-secret metadata and persists it under registry ownership.</summary>
    public ProfileMetadata Add(ProfileMetadata profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (SyncRoot)
            return AddUnderLock(profile);
    }

    public void Rename(Guid id, string nickname)
    {
        lock (SyncRoot)
        {
            var profile = UpdateUnderLock(id, item => item.Nickname = nickname?.Trim() ?? string.Empty);
            if (profile is not null)
                _audit.Record("rename", "ok", profile.DisplayName);
        }
    }

    /// <summary>
    /// Persists a nickname only if the committed registry generation is still the one observed
    /// before a remote CAS mutation. A failed save restores the complete previous profile and
    /// does not advance the generation.
    /// </summary>
    public bool TryRenameIfGeneration(Guid id, string nickname, long expectedGeneration)
    {
        lock (SyncRoot)
        {
            if (_currentGeneration != expectedGeneration)
                return false;

            var profile = UpdateUnderLock(id, item => item.Nickname = nickname?.Trim() ?? string.Empty);
            if (profile is null)
                return false;

            _audit.Record("rename", "ok", profile.DisplayName);
            return true;
        }
    }

    public void UpdateSubscriptionTracking(Guid id, SubscriptionTracking? tracking)
    {
        lock (SyncRoot)
        {
            var profile = UpdateUnderLock(id, item => item.SubscriptionTracking = tracking is null ? null : tracking with { });
            if (profile is not null)
                _audit.Record("subscription-tracking", "update", profile.DisplayName);
        }
    }

    public void MarkNeedsReLogin(Guid id)
    {
        lock (SyncRoot)
        {
            UpdateUnderLock(id, profile =>
            {
                profile.HealthStatus = HealthStatus.NeedsReLogin;
                profile.LastError = ErrorInfo.Create(
                    ErrorCategory.RefreshTokenExpired,
                    "Marcado manualmente como precisa re-login.",
                    _clock.UtcNow);
            });
        }
    }

    public void MarkUsed(Guid id)
    {
        lock (SyncRoot)
        {
            var profile = UpdateUnderLock(id, item => item.MarkedUsedAt = _clock.UtcNow);
            if (profile is not null)
                _audit.Record("mark-used", "ok", profile.DisplayName);
        }
    }

    public void UnmarkUsed(Guid id)
    {
        lock (SyncRoot)
        {
            var profile = UpdateUnderLock(id, item => item.MarkedUsedAt = null);
            if (profile is not null)
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
            var previous = _profiles;
            var reordered = previous.Select(profile => profile.CreateSnapshot()).ToList();
            var order = 0;
            foreach (var id in orderedIds)
            {
                var index = reordered.FindIndex(profile => profile.Id == id);
                if (index < 0)
                    continue;

                reordered[index].SortOrder = order++;
            }

            _profiles = reordered;
            try
            {
                SaveUnderLock();
            }
            catch
            {
                _profiles = previous;
                throw;
            }

            _audit.Record("reorder", "ok");
        }
    }

    internal IReadOnlyList<ProfileMetadata> SnapshotUnderLock() =>
        Array.AsReadOnly(_profiles.Select(profile => profile.CreateSnapshot()).ToArray());

    internal ProfileMetadata? FindUnderLock(Guid id) =>
        _profiles.FirstOrDefault(profile => profile.Id == id)?.CreateSnapshot();

    internal ProfileMetadata? FindByAccountSubUnderLock(string? accountSub) =>
        string.IsNullOrEmpty(accountSub)
            ? null
            : _profiles.FirstOrDefault(profile => profile.AccountSub == accountSub)?.CreateSnapshot();

    internal int NextSortOrderUnderLock() =>
        _profiles.Count == 0 ? 0 : _profiles.Max(profile => profile.SortOrder) + 1;

    internal bool ContainsUnderLock(Guid id) => _profiles.Any(profile => profile.Id == id);

    internal IReadOnlySet<Guid> KnownIdsUnderLock() => _profiles.Select(profile => profile.Id).ToHashSet();

    internal ProfileMetadata? SetSortOrderUnderLock(Guid id, int sortOrder) =>
        UpdateUnderLock(id, profile => profile.SortOrder = sortOrder);

    /// <summary>
    /// Applies a metadata change to a detached working copy and commits it through the registry.
    /// The callback never receives an instance owned by the registry.
    /// </summary>
    internal ProfileMetadata? UpdateUnderLock(
        Guid id,
        Action<ProfileMetadata> update,
        ProfileSaveIntent intent = ProfileSaveIntent.NormalUpdate)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (SyncRoot)
        {
            var index = _profiles.FindIndex(profile => profile.Id == id);
            if (index < 0)
                return null;

            var previous = _profiles[index];
            var updated = previous.CreateSnapshot();
            update(updated);
            _profiles[index] = updated;
            try
            {
                SaveUnderLock(intent);
            }
            catch
            {
                _profiles[index] = previous;
                throw;
            }

            return updated.CreateSnapshot();
        }
    }

    internal ProfileMetadata AddUnderLock(ProfileMetadata profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (SyncRoot)
        {
            if (ContainsUnderLock(profile.Id))
                throw new InvalidOperationException($"Account ID '{profile.Id}' is already registered.");

            var ownedProfile = profile.CreateSnapshot();
            _profiles.Add(ownedProfile);
            try
            {
                SaveUnderLock();
            }
            catch
            {
                _profiles.Remove(ownedProfile);
                throw;
            }

            return ownedProfile.CreateSnapshot();
        }
    }

    internal void AddManyUnderLock(IReadOnlyList<ProfileMetadata> profiles, ProfileSaveIntent intent)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0)
            return;

        lock (SyncRoot)
        {
            var incomingIds = profiles.Select(profile => profile.Id).ToHashSet();
            if (incomingIds.Count != profiles.Count || incomingIds.Any(ContainsUnderLock))
                throw new InvalidOperationException("One or more account IDs are already registered.");

            var added = profiles.Select(profile => profile.CreateSnapshot()).ToList();
            _profiles.AddRange(added);
            try
            {
                SaveUnderLock(intent);
            }
            catch
            {
                _profiles.RemoveAll(profile => incomingIds.Contains(profile.Id));
                throw;
            }
        }
    }

    internal void UpdateManyUnderLock(IReadOnlyDictionary<Guid, Action<ProfileMetadata>> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
            return;

        lock (SyncRoot)
        {
            var previous = _profiles;
            var updated = previous.Select(profile => profile.CreateSnapshot()).ToList();
            foreach (var (id, update) in updates)
            {
                var index = updated.FindIndex(profile => profile.Id == id);
                if (index >= 0)
                    update(updated[index]);
            }

            _profiles = updated;
            try
            {
                SaveUnderLock();
            }
            catch
            {
                _profiles = previous;
                throw;
            }
        }
    }

    internal void RemoveUnderLock(Guid id, ProfileSaveIntent intent)
    {
        lock (SyncRoot)
        {
            var index = _profiles.FindIndex(profile => profile.Id == id);
            if (index < 0)
                return;

            var removed = _profiles[index];
            _profiles.RemoveAt(index);
            try
            {
                SaveUnderLock(intent);
            }
            catch
            {
                _profiles.Insert(index, removed);
                throw;
            }
        }
    }

    internal void SetActiveProfileUnderLock(Guid? activeId)
    {
        lock (SyncRoot)
        {
            if (_profiles.All(profile => profile.IsActive == (activeId == profile.Id)))
                return;

            var previous = _profiles;
            _profiles = previous.Select(profile =>
            {
                var copy = profile.CreateSnapshot();
                copy.IsActive = activeId == copy.Id;
                return copy;
            }).ToList();

            try
            {
                SaveUnderLock();
            }
            catch
            {
                _profiles = previous;
                throw;
            }
        }
    }

    internal void LoadUnderLock()
    {
        var previousProfiles = _profiles;
        var wasLoaded = _isLoaded;
        _isLoading = true;
        try
        {
            _profiles = _store.LoadAll().Select(profile => profile.CreateSnapshot()).ToList();
            EnsureSortOrderInitializedUnderLock();
            _isLoaded = true;
            _currentGeneration++;
        }
        catch
        {
            _profiles = previousProfiles;
            _isLoaded = wasLoaded;
            throw;
        }
        finally
        {
            _isLoading = false;
        }

        NotifySnapshotChanged();
    }

    private void SaveUnderLock(ProfileSaveIntent intent = ProfileSaveIntent.NormalUpdate)
    {
        _store.SaveAll(_profiles, intent);
        if (!_isLoading)
        {
            _currentGeneration++;
            NotifySnapshotChanged();
        }
    }

    private void NotifySnapshotChanged()
    {
        var handlers = SnapshotChanged;
        if (handlers is null)
            return;

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch
            {
                // A background observer must never turn a successful profile commit into a failure.
            }
        }
    }

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
