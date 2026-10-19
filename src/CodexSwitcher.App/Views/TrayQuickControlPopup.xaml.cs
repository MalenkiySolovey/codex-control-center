using CodexSwitcher.App.Features.Launch;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Views;

public sealed partial class TrayQuickControlPopup : UserControl
{
    public Strings Loc => Strings.Current;

    public event Action? OpenSwitchboardRequested;
    public event Action? RefreshAllRequested;
    public event Action? OpenCodexRequested;
    public event Action<AccountItemViewModel>? AccountSwitchRequested;
    public event Action<ApiProviderItemViewModel>? ApiProviderSwitchRequested;
    public event Action<CodexLaunchProfileItemViewModel>? LaunchProfileToggleRequested;
    public event Action? ManageLaunchProfilesRequested;
    public event Action? OpenSettingsRequested;
    public event Action? QuitRequested;

    public TrayQuickControlPopup() => InitializeComponent();

    private void OpenSwitchboard_Click(object sender, RoutedEventArgs e) => OpenSwitchboardRequested?.Invoke();
    private void RefreshAll_Click(object sender, RoutedEventArgs e) => RefreshAllRequested?.Invoke();
    private void OpenCodex_Click(object sender, RoutedEventArgs e) => OpenCodexRequested?.Invoke();
    private void ManageLaunchProfiles_Click(object sender, RoutedEventArgs e) => ManageLaunchProfilesRequested?.Invoke();
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsRequested?.Invoke();
    private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountItemViewModel account })
        {
            AccountSwitchRequested?.Invoke(account);
        }
    }

    private void ApiProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ApiProviderItemViewModel provider })
        {
            ApiProviderSwitchRequested?.Invoke(provider);
        }
    }

    private void LaunchProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CodexLaunchProfileItemViewModel profile })
        {
            LaunchProfileToggleRequested?.Invoke(profile);
        }
    }
}
