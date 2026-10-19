namespace CodexSwitcher.Core.Tests.TestSupport;

/// <summary>Builds the production account-service graph for focused service and compatibility tests.</summary>
public sealed record AccountServiceGraph(
    AccountRegistry Registry,
    AccountCredentialService Credentials,
    AccountTransferService Transfer,
    CodexAuthReconciler AuthReconciler,
    ProfileService Profiles)
{
    public static AccountServiceGraph Create(
        IVaultService vault,
        IProfileStore store,
        ReconciliationService matcher,
        IFileSystem fileSystem,
        CodexPaths paths,
        IClock clock,
        IAuditLog audit,
        ITotpCredentialStore? totpStore = null)
    {
        var registry = new AccountRegistry(store, clock, audit);
        var credentials = new AccountCredentialService(vault, registry, clock, audit, totpStore);
        var transfer = new AccountTransferService(registry, credentials, audit);
        var authReconciler = new CodexAuthReconciler(registry, credentials, matcher, fileSystem, paths, clock, audit);
        var profiles = new ProfileService(registry, credentials, transfer, authReconciler);
        return new AccountServiceGraph(registry, credentials, transfer, authReconciler, profiles);
    }
}
