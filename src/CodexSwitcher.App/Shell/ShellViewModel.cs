using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
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
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
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
using CodexSwitcher.App.Features.Accounts;
using CodexSwitcher.App.Features.Providers;
using CodexSwitcher.App.Features.Launch;
using CodexSwitcher.App.Shell.Routing;
using CodexSwitcher.App.Shell.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexSwitcher.App.Shell;

/// <summary>
/// Shell coordinator ViewModel: manages top-level navigation tabs, window-level lifecycle delegation,
/// and composes feature slices (<see cref="AccountsViewModel"/> and <see cref="ApiProvidersViewModel"/>)
/// alongside shared presentation state (<see cref="IAppNotificationService"/>, <see cref="IAppBusyService"/>,
/// and <see cref="ActiveTargetPresentationCoordinator"/>).
/// Holds zero dependencies on backend storage, credential vaults, or filesystem paths.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    public AccountsViewModel Accounts { get; }
    public ApiProvidersViewModel ApiProviders { get; }
    public LaunchProfilesViewModel LaunchProfiles { get; }
    public ActiveTargetPresentationCoordinator Routing { get; }
    public IAppNotificationService Notifications { get; }
    public IAppBusyService Busy { get; }

    [ObservableProperty] public partial int SelectedTab { get; set; }
    [ObservableProperty] public partial bool IsApiProvidersTabLoaded { get; set; }
    public bool ShowAccountsHeader => SelectedTab == 0;
    public bool ShowApiProvidersHeader => SelectedTab == 1;
    public bool ShowLaunchProfilesHeader => SelectedTab == 2;

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(ShowAccountsHeader));
        OnPropertyChanged(nameof(ShowApiProvidersHeader));
        OnPropertyChanged(nameof(ShowLaunchProfilesHeader));
        if (value == 1)
        {
            IsApiProvidersTabLoaded = true;
        }
    }

    public ShellViewModel(
        AccountsViewModel accounts,
        ApiProvidersViewModel apiProviders,
        LaunchProfilesViewModel launchProfiles,
        ActiveTargetPresentationCoordinator routing,
        IAppNotificationService notifications,
        IAppBusyService busy)
    {
        Accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        ApiProviders = apiProviders ?? throw new ArgumentNullException(nameof(apiProviders));
        LaunchProfiles = launchProfiles ?? throw new ArgumentNullException(nameof(launchProfiles));
        Routing = routing ?? throw new ArgumentNullException(nameof(routing));
        Notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        Busy = busy ?? throw new ArgumentNullException(nameof(busy));

        Accounts.TargetStateChanged += (_, _) => RefreshRoutingSummary();
        ApiProviders.TargetStateChanged += (_, _) => RefreshRoutingSummary();
        Accounts.TargetStateChanged += (_, _) => LaunchProfiles.RefreshTargets();
        ApiProviders.TargetStateChanged += (_, _) => LaunchProfiles.RefreshTargets();
        Accounts.OrderChanged += (_, _) => LaunchProfiles.RefreshTargets();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        await Accounts.LoadCommand.ExecuteAsync(null);
        LaunchProfiles.Load();
        RefreshRoutingSummary();
    }

    public void RefreshRoutingSummary()
    {
        var activeTarget = Routing.RefreshRouting(Accounts.ActiveProfile);
        Accounts.Rebuild(activeTarget, Routing.IsRoutingActiveToApi);
        ApiProviders.Rebuild(activeTarget, Routing.IsRoutingActiveToApi);
    }

    [RelayCommand]
    private void SelectChatGptTab() => SelectedTab = 0;

    [RelayCommand]
    private void SelectApiProvidersTab() => SelectedTab = 1;

    [RelayCommand]
    private void SelectLaunchProfilesTab() => SelectedTab = 2;

    public void SetForegroundActive(bool active) => Accounts.SetForegroundActive(active);

    public void HideAllRevealedTotp() => Accounts.HideAllRevealedTotp();

    public void Cleanup() => Accounts.Cleanup();

    public void Dispose()
    {
        Accounts.Dispose();
        ApiProviders.Dispose();
        LaunchProfiles.Dispose();
    }
}
