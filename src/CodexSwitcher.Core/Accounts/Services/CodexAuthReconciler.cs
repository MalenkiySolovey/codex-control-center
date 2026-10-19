using System.Security.Cryptography;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Security.Secrets;

namespace CodexSwitcher.Core.Accounts.Services;

/// <summary>
/// Owns the Codex-specific relationship between registry metadata and CODEX_HOME/auth.json:
/// active-account matching, unmanaged-account discovery/adoption, drift write-back, and
/// detected-subscription backfill from the active slot or encrypted credential vault.
/// </summary>
public sealed class CodexAuthReconciler
{
    private readonly AccountRegistry _registry;
    private readonly AccountCredentialService _credentials;
    private readonly ReconciliationService _matcher;
    private readonly IFileSystem _fs;
    private readonly CodexPaths _paths;
    private readonly IClock _clock;
    private readonly IAuditLog _audit;

    public CodexAuthReconciler(
        AccountRegistry registry,
        AccountCredentialService credentials,
        ReconciliationService matcher,
        IFileSystem fs,
        CodexPaths paths,
        IClock clock,
        IAuditLog audit)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public ReconciliationResult Load(bool auditOrphans = false)
    {
        lock (_registry.SyncRoot)
        {
            _registry.LoadUnderLock();
            EnsureDetectedSubscriptionsUnderLock();
            if (auditOrphans)
                _credentials.AuditOrphanVaultBlobs();

            return ReconcileUnderLock();
        }
    }

    public void EnsureDetectedSubscriptions()
    {
        lock (_registry.SyncRoot)
            EnsureDetectedSubscriptionsUnderLock();
    }

    public ReconciliationResult Reconcile()
    {
        lock (_registry.SyncRoot)
            return ReconcileUnderLock();
    }

    public bool HasUnmanagedActiveAccount()
    {
        lock (_registry.SyncRoot)
        {
            if (!_fs.FileExists(_paths.ActiveAuthPath))
                return false;

            var result = _matcher.Reconcile(_registry.SnapshotUnderLock());
            return result is { Match: ActiveMatch.None, ActiveFingerprint: not null };
        }
    }

    /// <summary>Returns only local email/plan metadata; credential tokens are never exposed.</summary>
    public (bool Detected, string? Email, string? PlanType) GetUnmanagedActiveAccountInfo()
    {
        if (!HasUnmanagedActiveAccount())
            return (false, null, null);

        byte[]? authJson = null;
        try
        {
            authJson = _fs.ReadAllBytes(_paths.ActiveAuthPath);
            var (_, claims) = AuthJsonReader.Identify(authJson);
            return (true, claims.Email, claims.PlanType);
        }
        catch
        {
            return (true, null, null);
        }
        finally
        {
            if (authJson is not null)
                CryptographicOperations.ZeroMemory(authJson);
        }
    }

    public ProfileMetadata? AdoptActiveAccount(string? nickname = null)
    {
        if (!_fs.FileExists(_paths.ActiveAuthPath))
            return null;

        var authJson = _fs.ReadAllBytes(_paths.ActiveAuthPath);
        try
        {
            var profile = _credentials.AddFromAuthJson(authJson, nickname);
            lock (_registry.SyncRoot)
            {
                _registry.SetActiveProfileUnderLock(profile.Id);
            }

            _audit.Record("adopt", "ok", profile.DisplayName);
            return _registry.Find(profile.Id);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authJson);
        }
    }

    public ProfileMetadata Reauthenticate(Guid id, byte[] authJson) =>
        _credentials.Reauthenticate(
            id,
            authJson,
            activeAuthJson => _fs.WriteAllBytesAtomic(_paths.ActiveAuthPath, activeAuthJson));

    public IReadOnlyList<Guid> DetectOrphanVaultBlobs() => _credentials.DetectOrphanVaultBlobs();

    public int RecoverOrphanProfiles() => _credentials.RecoverOrphanProfiles();

    private ReconciliationResult ReconcileUnderLock()
    {
        var profiles = _registry.SnapshotUnderLock();
        var result = _matcher.Reconcile(profiles);
        var activeId = profiles.FirstOrDefault(profile => profile.IsActive)?.Id;
        _registry.SetActiveProfileUnderLock(activeId);

        if (result.Match == ActiveMatch.SameAccountDrifted && result.ActiveProfileId is { } id)
        {
            var profile = _registry.FindUnderLock(id);
            if (profile is not null && _fs.FileExists(_paths.ActiveAuthPath))
            {
                var activeAuthJson = _fs.ReadAllBytes(_paths.ActiveAuthPath);
                try
                {
                    _credentials.PersistReconciledCredential(id, activeAuthJson);
                    _audit.Record("reconcile", "write-back", profile.DisplayName);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(activeAuthJson);
                }
            }
        }

        return result;
    }

    private void EnsureDetectedSubscriptionsUnderLock()
    {
        var now = _clock.UtcNow;

        foreach (var profile in _registry.SnapshotUnderLock())
        {
            if (profile.DetectedSubscription is not null && !string.IsNullOrWhiteSpace(profile.DetectedDisplayName))
                continue;

            var changed = false;
            try
            {
                if (profile.IsActive && _fs.FileExists(_paths.ActiveAuthPath))
                {
                    var activeAuthJson = _fs.ReadAllBytes(_paths.ActiveAuthPath);
                    try
                    {
                        changed |= AccountCredentialService.EnrichMissingDetectedMetadata(profile, activeAuthJson, now);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(activeAuthJson);
                    }
                }

                if ((profile.DetectedSubscription is null || string.IsNullOrWhiteSpace(profile.DetectedDisplayName)) &&
                    _credentials.Exists(profile.Id))
                {
                    var storedAuthJson = _credentials.LoadCredential(profile.Id);
                    try
                    {
                        changed |= AccountCredentialService.EnrichMissingDetectedMetadata(profile, storedAuthJson, now);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(storedAuthJson);
                    }
                }
            }
            catch
            {
                // A transient local read/decryption failure must not fail account loading.
            }

            if (changed)
            {
                _registry.UpdateUnderLock(profile.Id, current =>
                {
                    current.DetectedSubscription = profile.DetectedSubscription;
                    current.DetectedDisplayName = profile.DetectedDisplayName;
                });
            }
        }
    }
}
