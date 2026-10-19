using System.Security.Cryptography;
using System.Text.Json;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;

namespace CodexSwitcher.Core.Accounts.Services;

/// <summary>
/// Owns account credential validation and operations against the encrypted vault. Account
/// metadata is updated through <see cref="AccountRegistry"/>; Codex auth-slot I/O stays in
/// <see cref="CodexAuthReconciler"/>.
/// </summary>
public sealed class AccountCredentialService
{
    private readonly IVaultService _vault;
    private readonly AccountRegistry _registry;
    private readonly IClock _clock;
    private readonly IAuditLog _audit;
    private readonly ITotpCredentialStore? _totpStore;

    public AccountCredentialService(
        IVaultService vault,
        AccountRegistry registry,
        IClock clock,
        IAuditLog audit,
        ITotpCredentialStore? totpStore = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _totpStore = totpStore;
    }

    /// <summary>Validates a Codex auth document without persisting or exposing its contents.</summary>
    public static void ValidateAuthJson(byte[] authJson)
    {
        ArgumentNullException.ThrowIfNull(authJson);
        var (file, _) = AuthJsonReader.Identify(authJson);
        if (!IsRecognizableAuthFile(file))
            throw new InvalidDataException("O arquivo selecionado não é um auth.json válido do Codex.");
    }

    internal static bool IsRecognizableAuthJson(byte[] authJson) =>
        authJson is not null && IsRecognizableAuthFile(AuthJsonReader.TryRead(authJson));

    public ProfileMetadata AddFromAuthJson(byte[] authJson, string? nickname)
    {
        ArgumentNullException.ThrowIfNull(authJson);
        var (file, claims) = AuthJsonReader.Identify(authJson);
        if (!IsRecognizableAuthFile(file))
            throw new InvalidDataException("O arquivo selecionado não é um auth.json válido do Codex.");

        lock (_registry.SyncRoot)
        {
            var existing = _registry.FindByAccountSubUnderLock(claims.Sub);
            if (existing is not null)
            {
                existing.BlobFingerprint = _vault.SaveBlob(existing.Id, authJson);
                existing.HealthStatus = HealthStatus.Valid;
                existing.LastError = null;
                existing.LastRefreshedAt = file?.LastRefresh ?? _clock.UtcNow;
                SubscriptionMetadataService.ApplyCurrentCredential(existing, authJson, _clock.UtcNow);
                if (!string.IsNullOrWhiteSpace(nickname))
                    existing.Nickname = nickname.Trim();

                _registry.SaveUnderLock();
                _audit.Record("add", "updated-existing", existing.DisplayName);
                return existing;
            }

            var profile = new ProfileMetadata
            {
                Nickname = nickname?.Trim() ?? string.Empty,
                AccountEmail = claims.Email,
                DetectedDisplayName = claims.DisplayName,
                AccountSub = claims.Sub,
                AuthMode = file?.AuthMode,
                PlanType = claims.PlanType,
                CreatedAt = _clock.UtcNow,
                LastRefreshedAt = file?.LastRefresh ?? _clock.UtcNow,
                HealthStatus = HealthStatus.Valid,
                SortOrder = _registry.MutableProfiles.Count == 0 ? 0 : _registry.MutableProfiles.Max(item => item.SortOrder) + 1,
            };
            SubscriptionMetadataService.ApplyCurrentCredential(profile, authJson, _clock.UtcNow);
            profile.BlobFingerprint = _vault.SaveBlob(profile.Id, authJson);
            _registry.AddUnderLock(profile);
            _audit.Record("add", "ok", profile.DisplayName);
            return profile;
        }
    }

    /// <summary>
    /// Reauthenticates a profile. The optional callback is supplied by the Codex auth reconciler
    /// so an active slot is written atomically before metadata is committed, without giving this
    /// service a CODEX_HOME path dependency.
    /// </summary>
    internal ProfileMetadata Reauthenticate(
        Guid id,
        byte[] authJson,
        Action<byte[]>? writeActiveSlotBeforeMetadataSave)
    {
        ArgumentNullException.ThrowIfNull(authJson);
        var (file, claims) = AuthJsonReader.Identify(authJson);
        if (!IsRecognizableAuthFile(file))
            throw new InvalidDataException("O arquivo fornecido não é um auth.json válido do Codex.");

        lock (_registry.SyncRoot)
        {
            var profile = _registry.FindUnderLock(id)
                ?? throw new InvalidOperationException("Perfil não encontrado.");

            if (!string.IsNullOrEmpty(profile.AccountSub) &&
                !string.IsNullOrEmpty(claims.Sub) &&
                profile.AccountSub != claims.Sub)
            {
                var duplicate = _registry.FindByAccountSubUnderLock(claims.Sub);
                if (duplicate is not null && duplicate.Id != id)
                    throw new InvalidOperationException($"Esta conta já está cadastrada como '{duplicate.DisplayName}'.");
            }

            profile.BlobFingerprint = _vault.SaveBlob(profile.Id, authJson);
            if (!string.IsNullOrEmpty(claims.Sub)) profile.AccountSub = claims.Sub;
            if (!string.IsNullOrEmpty(claims.Email)) profile.AccountEmail = claims.Email;
            if (!string.IsNullOrEmpty(claims.PlanType)) profile.PlanType = claims.PlanType;
            if (!string.IsNullOrEmpty(file?.AuthMode)) profile.AuthMode = file.AuthMode;
            profile.HealthStatus = HealthStatus.Valid;
            profile.LastError = null;
            profile.LastRefreshedAt = file?.LastRefresh ?? _clock.UtcNow;
            SubscriptionMetadataService.ApplyCurrentCredential(profile, authJson, _clock.UtcNow);

            if (profile.IsActive)
                writeActiveSlotBeforeMetadataSave?.Invoke(authJson);

            _registry.SaveUnderLock();
            _audit.Record("reauthenticate", "ok", profile.DisplayName);
            return profile;
        }
    }

    /// <summary>
    /// Persists a refresh from an isolated long-running CLI session. The caller must hold the
    /// profile operation lock; this local fingerprint comparison prevents stale write-back.
    /// </summary>
    public bool PersistIsolatedLaunchRefreshUnderLock(
        Guid profileId,
        string expectedFingerprint,
        byte[] refreshedAuthJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        ArgumentNullException.ThrowIfNull(refreshedAuthJson);

        using (var refreshedDocument = JsonDocument.Parse(refreshedAuthJson))
        {
            if (refreshedDocument.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The refreshed Codex credential is not a JSON object.");
        }

        lock (_registry.SyncRoot)
        {
            if (_registry.Profiles.Count == 0)
                _registry.LoadUnderLock();

            var profile = _registry.FindUnderLock(profileId);
            if (profile is null || profile.IsActive || !_vault.Exists(profileId))
                return false;

            var currentAuthJson = _vault.LoadBlob(profileId);
            try
            {
                if (!string.Equals(
                        Fingerprint.Compute(currentAuthJson),
                        expectedFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(currentAuthJson);
            }

            profile.BlobFingerprint = _vault.SaveBlob(profileId, refreshedAuthJson);
            profile.LastRefreshedAt = _clock.UtcNow;
            profile.HealthStatus = HealthStatus.Valid;
            profile.LastError = null;
            _registry.SaveUnderLock();
            return true;
        }
    }

    public void Remove(Guid id)
    {
        lock (_registry.SyncRoot)
        {
            var profile = _registry.FindUnderLock(id);
            if (profile is null)
                return;

            _vault.DeleteBlob(id);
            _totpStore?.Delete(id);
            _registry.RemoveUnderLock(profile, ProfileSaveIntent.ExplicitDelete);
            _audit.Record("remove", "ok", profile.DisplayName);
        }
    }

    public IReadOnlyList<Guid> DetectOrphanVaultBlobs()
    {
        lock (_registry.SyncRoot)
        {
            var knownIds = _registry.Profiles.Select(profile => profile.Id).ToHashSet();
            return _vault.EnumerateBlobs().Where(id => !knownIds.Contains(id)).ToList();
        }
    }

    public void AuditOrphanVaultBlobs()
    {
        var orphanBlobs = DetectOrphanVaultBlobs();
        if (orphanBlobs.Count > 0)
            _audit.Record("vault-audit", "orphans-detected", $"{orphanBlobs.Count} orphan vault blob(s) detected.");
    }

    public int RecoverOrphanProfiles()
    {
        lock (_registry.SyncRoot)
        {
            var orphans = DetectOrphanVaultBlobs();
            if (orphans.Count == 0)
                return 0;

            var recovered = 0;
            foreach (var orphanId in orphans)
            {
                byte[]? authJson = null;
                try
                {
                    authJson = _vault.LoadBlob(orphanId);
                    var (file, claims) = AuthJsonReader.Identify(authJson);
                    if (file is null && string.IsNullOrEmpty(claims.Sub))
                        continue;

                    var profile = new ProfileMetadata
                    {
                        Id = orphanId,
                        Nickname = string.Empty,
                        AccountEmail = claims.Email,
                        DetectedDisplayName = claims.DisplayName,
                        AccountSub = claims.Sub,
                        AuthMode = file?.AuthMode ?? "chatgpt",
                        PlanType = claims.PlanType ?? "free",
                        CreatedAt = file?.LastRefresh ?? _clock.UtcNow,
                        LastRefreshedAt = file?.LastRefresh,
                        HealthStatus = HealthStatus.Valid,
                        SortOrder = _registry.MutableProfiles.Count == 0 ? 0 : _registry.MutableProfiles.Max(item => item.SortOrder) + 1,
                        BlobFingerprint = Fingerprint.Compute(authJson),
                    };
                    SubscriptionMetadataService.ApplyCurrentCredential(profile, authJson, _clock.UtcNow);
                    _registry.MutableProfiles.Add(profile);
                    recovered++;
                }
                catch (Exception ex)
                {
                    _audit.Record("recovery", "failed-orphan", $"{orphanId:N}: {ex.Message}");
                }
                finally
                {
                    if (authJson is not null)
                        CryptographicOperations.ZeroMemory(authJson);
                }
            }

            if (recovered > 0)
            {
                _registry.SaveUnderLock(ProfileSaveIntent.NormalUpdate);
                _audit.Record("recovery", "ok", $"{recovered} orphan profile(s) recovered.");
            }

            return recovered;
        }
    }

    internal bool Exists(Guid profileId) => _vault.Exists(profileId);

    internal byte[] LoadCredential(Guid profileId) => _vault.LoadBlob(profileId);

    internal static bool EnrichMissingDetectedMetadata(ProfileMetadata profile, byte[] authJson, DateTimeOffset now) =>
        SubscriptionMetadataService.EnrichMissing(profile, authJson, now);

    internal void PersistReconciledCredential(ProfileMetadata profile, byte[] authJson)
    {
        lock (_registry.SyncRoot)
        {
            profile.BlobFingerprint = _vault.SaveBlob(profile.Id, authJson);
            var info = AuthJsonReader.TryRead(authJson);
            if (info?.LastRefresh is { } lastRefresh)
                profile.LastRefreshedAt = lastRefresh;
            if (profile.HealthStatus is HealthStatus.Unknown)
                profile.HealthStatus = HealthStatus.Valid;
            SubscriptionMetadataService.ApplyCurrentCredential(profile, authJson, _clock.UtcNow);
            _registry.SaveUnderLock();
        }
    }

    private static bool IsRecognizableAuthFile(AuthFileInfo? file) =>
        file is not null && (!string.IsNullOrWhiteSpace(file.AuthMode) || !string.IsNullOrWhiteSpace(file.IdToken));
}
