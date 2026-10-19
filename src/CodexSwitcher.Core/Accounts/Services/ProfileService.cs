using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Accounts.Services;

/// <summary>
/// Thin compatibility facade retained for older integrations and tests. Production consumers
/// must depend on the narrow account registry, credential, transfer, and reconciliation owners.
/// </summary>
public sealed class ProfileService
{
    private readonly AccountRegistry _registry;
    private readonly AccountCredentialService _credentials;
    private readonly AccountTransferService _transfer;
    private readonly CodexAuthReconciler _authReconciler;

    public ProfileService(
        AccountRegistry registry,
        AccountCredentialService credentials,
        AccountTransferService transfer,
        CodexAuthReconciler authReconciler)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
        _authReconciler = authReconciler ?? throw new ArgumentNullException(nameof(authReconciler));
    }

    public ReconciliationResult Load(bool auditOrphans = false) => _authReconciler.Load(auditOrphans);

    public void AuditOrphanVaultBlobs() => _credentials.AuditOrphanVaultBlobs();

    public bool PersistIsolatedLaunchRefreshUnderLock(Guid profileId, string expectedFingerprint, byte[] refreshedAuthJson) =>
        _credentials.PersistIsolatedLaunchRefreshUnderLock(profileId, expectedFingerprint, refreshedAuthJson);

    public void EnsureDetectedSubscriptions() => _authReconciler.EnsureDetectedSubscriptions();

    public ReconciliationResult Reconcile() => _authReconciler.Reconcile();

    public bool HasUnmanagedActiveAccount() => _authReconciler.HasUnmanagedActiveAccount();

    public (bool Detected, string? Email, string? PlanType) GetUnmanagedActiveAccountInfo() =>
        _authReconciler.GetUnmanagedActiveAccountInfo();

    public ProfileMetadata? AdoptActiveAccount(string? nickname = null) => _authReconciler.AdoptActiveAccount(nickname);

    public ProfileMetadata AddFromAuthJson(byte[] authJson, string? nickname) => _credentials.AddFromAuthJson(authJson, nickname);

    public string ExportOne(Guid id) => _transfer.ExportOne(id);

    public string ExportAll() => _transfer.ExportAll();

    public int Import(string document) => _transfer.Import(document);

    public void Rename(Guid id, string nickname) => _registry.Rename(id, nickname);

    public void UpdateSubscriptionTracking(Guid id, SubscriptionTracking? tracking) =>
        _registry.UpdateSubscriptionTracking(id, tracking);

    public void Remove(Guid id) => _credentials.Remove(id);

    public IReadOnlyList<Guid> DetectOrphanVaultBlobs() => _authReconciler.DetectOrphanVaultBlobs();

    public int RecoverOrphanProfiles() => _authReconciler.RecoverOrphanProfiles();

    public void MarkNeedsReLogin(Guid id) => _registry.MarkNeedsReLogin(id);

    public ProfileMetadata Reauthenticate(Guid id, byte[] authJson) => _authReconciler.Reauthenticate(id, authJson);

    public void MarkUsed(Guid id) => _registry.MarkUsed(id);

    public void UnmarkUsed(Guid id) => _registry.UnmarkUsed(id);

    public void Reorder(IReadOnlyList<Guid> orderedIds) => _registry.Reorder(orderedIds);

}
