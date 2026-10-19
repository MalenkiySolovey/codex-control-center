using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Transfer.Models;

namespace CodexSwitcher.Core.Accounts.Services;

/// <summary>
/// Owns the portable account document format and its import/export lifecycle. Export documents
/// contain plaintext credentials and must be handled by callers as secrets.
/// </summary>
public sealed class AccountTransferService
{
    private static readonly JsonSerializerOptions TransferJsonOptions = new() { WriteIndented = true };

    private readonly AccountRegistry _registry;
    private readonly AccountCredentialService _credentials;
    private readonly IAuditLog _audit;

    public AccountTransferService(AccountRegistry registry, AccountCredentialService credentials, IAuditLog audit)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public string ExportOne(Guid id)
    {
        lock (_registry.SyncRoot)
        {
            var profile = _registry.FindUnderLock(id)
                ?? throw new InvalidOperationException("Perfil não encontrado.");
            return SerializeTransfer([ToTransferredAccount(profile)]);
        }
    }

    public string ExportAll()
    {
        lock (_registry.SyncRoot)
        {
            return SerializeTransfer(_registry.Profiles
                .OrderBy(profile => profile.SortOrder)
                .Select(ToTransferredAccount)
                .ToList());
        }
    }

    /// <summary>
    /// Imports a transfer document or a legacy raw auth.json. All entries are validated before
    /// any profile is mutated, preserving the existing all-inputs-checked-first behavior.
    /// </summary>
    public int Import(string document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);

        AccountTransferDocument? transfer;
        try
        {
            transfer = JsonSerializer.Deserialize<AccountTransferDocument>(document);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("O arquivo de importação não é válido.", ex);
        }

        if (transfer is null || transfer.Accounts.Count == 0)
        {
            var authJson = Encoding.UTF8.GetBytes(document);
            try
            {
                if (!AccountCredentialService.IsRecognizableAuthJson(authJson))
                    throw new InvalidDataException("O arquivo de importação está vazio ou usa um formato incompatível.");

                _credentials.AddFromAuthJson(authJson, null);
                _audit.Record("import", "raw-auth-json");
                return 1;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(authJson);
            }
        }

        if (transfer.FormatVersion != AccountTransferDocument.CurrentFormatVersion)
            throw new InvalidDataException("O arquivo de importação usa um formato incompatível.");

        var validated = new List<(TransferredAccount Entry, byte[] AuthJson)>();
        try
        {
            foreach (var entry in transfer.Accounts)
            {
                byte[] authJson;
                try
                {
                    authJson = Convert.FromBase64String(entry.AuthJsonBase64);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("O arquivo de importação contém uma credencial inválida.", ex);
                }

                if (!AccountCredentialService.IsRecognizableAuthJson(authJson))
                {
                    CryptographicOperations.ZeroMemory(authJson);
                    throw new InvalidDataException("O arquivo de importação contém um auth.json inválido.");
                }

                validated.Add((entry, authJson));
            }

            foreach (var (entry, authJson) in validated)
            {
                var profile = _credentials.AddFromAuthJson(authJson, entry.Nickname);
                profile.SortOrder = entry.SortOrder;
            }

            _registry.Save();
            _audit.Record("import", "ok", transfer.Accounts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return transfer.Accounts.Count;
        }
        finally
        {
            foreach (var (_, authJson) in validated)
                CryptographicOperations.ZeroMemory(authJson);
        }
    }

    private TransferredAccount ToTransferredAccount(ProfileMetadata profile)
    {
        var authJson = _credentials.LoadCredential(profile.Id);
        try
        {
            return new TransferredAccount
            {
                Nickname = profile.Nickname,
                SortOrder = profile.SortOrder,
                AuthJsonBase64 = Convert.ToBase64String(authJson),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authJson);
        }
    }

    private static string SerializeTransfer(IReadOnlyList<TransferredAccount> accounts) =>
        JsonSerializer.Serialize(new AccountTransferDocument { Accounts = accounts.ToList() }, TransferJsonOptions);
}
