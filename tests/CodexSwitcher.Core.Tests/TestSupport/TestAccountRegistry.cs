using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Time;

namespace CodexSwitcher.Core.Tests.TestSupport;

internal static class TestAccountRegistry
{
    public static AccountRegistry CreateLoaded(IProfileStore store, IClock clock, IAuditLog audit)
    {
        var registry = new AccountRegistry(store, clock, audit);
        registry.Load();
        return registry;
    }
}
