using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.App.Features.Accounts;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexSwitcher.App.Shell.Routing;

/// <summary>
/// Presentation coordinator for active routing status.
/// Maps domain <see cref="ICodexActiveTargetResolver"/> results into UI-friendly summary properties.
/// Contains zero config.toml or auth.json mutation logic (pure read-only mapper).
/// </summary>
public sealed partial class ActiveTargetPresentationCoordinator : ObservableObject
{
    private readonly ICodexActiveTargetResolver? _activeTargetResolver;
    private readonly AppPaths? _paths;
    private readonly IAccountReadSnapshotProvider? _accountReadSnapshotProvider;
    private readonly Strings _loc = Strings.Current;

    [ObservableProperty] public partial string ActiveTargetSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial string ActiveTargetCredentialSlot { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsRoutingActiveToApi { get; set; }
    public ActiveTarget CurrentTarget { get; private set; }

    public ActiveTargetPresentationCoordinator(
        ICodexActiveTargetResolver? activeTargetResolver = null,
        AppPaths? paths = null,
        IAccountReadSnapshotProvider? accountReadSnapshotProvider = null)
    {
        _activeTargetResolver = activeTargetResolver;
        _paths = paths;
        _accountReadSnapshotProvider = accountReadSnapshotProvider;
        CurrentTarget = new ActiveTarget.ChatGpt(null, null);
    }

    public ActiveTarget RefreshRouting(ProfileMetadata? activeProfile)
    {
        var accountDisplayName = GetCanonicalDisplayName(activeProfile);
        var configTomlPath = _paths?.Codex.ConfigTomlPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

        var activeTarget = _activeTargetResolver?.ResolveActiveTarget(configTomlPath, activeProfile?.Id, activeProfile?.AccountEmail)
            ?? new ActiveTarget.ChatGpt(activeProfile?.Id, activeProfile?.AccountEmail);

        CurrentTarget = activeTarget;

        if (activeTarget is ActiveTarget.Api apiTarget)
        {
            IsRoutingActiveToApi = true;
            ActiveTargetSummary = $"{apiTarget.Profile.Nickname} ({apiTarget.Profile.GetEffectiveSelectedModel()})";
            ActiveTargetCredentialSlot = accountDisplayName;
        }
        else
        {
            IsRoutingActiveToApi = false;
            ActiveTargetSummary = accountDisplayName;
            ActiveTargetCredentialSlot = accountDisplayName;
        }

        return activeTarget;
    }

    private string GetCanonicalDisplayName(ProfileMetadata? activeProfile)
    {
        if (activeProfile is null)
            return _loc.NoManaged;

        if (_accountReadSnapshotProvider is not null)
        {
            var accountId = activeProfile.Id.ToString("D");
            var descriptor = _accountReadSnapshotProvider.Read().Accounts
                .FirstOrDefault(account => string.Equals(account.AccountId, accountId, StringComparison.Ordinal));
            if (descriptor is not null)
                return descriptor.DisplayName;
        }

        return activeProfile.DisplayName;
    }
}
