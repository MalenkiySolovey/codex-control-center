using System.Xml.Linq;

namespace CodexSwitcher.Core.Tests;

public sealed class TrayAndLifecycleArchitectureTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "CodexSwitcher.App", "Views", "TrayQuickControlPopup.xaml")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the repository source tree from the test output directory.");
        }
    }

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));

    private static string GetMethodBody(string source, string signature)
    {
        var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Method signature was not found: {signature}");

        var bodyStart = source.IndexOf('{', signatureIndex);
        Assert.True(bodyStart >= 0, $"Method body was not found: {signature}");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0)
            {
                return source[bodyStart..(index + 1)];
            }
        }

        throw new InvalidDataException($"Method body was not closed: {signature}");
    }

    [Fact]
    public void PopupUsesFullAccountCollectionAndCachedQuotaPresentation()
    {
        var popupPath = Path.Combine(RepositoryRoot, "src", "CodexSwitcher.App", "Views", "TrayQuickControlPopup.xaml");
        var popup = XDocument.Load(popupPath);
        var accountList = Assert.Single(popup.Descendants(), element =>
            element.Name.LocalName == "ItemsControl" &&
            (string?)element.Attribute("ItemsSource") == "{Binding Accounts.AllItems}");

        Assert.NotNull(accountList.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "TextBlock" &&
            (string?)element.Attribute("Text") == "{Binding Usage.StaleBadgeLabel}"));
        Assert.NotNull(accountList.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "ItemsControl" &&
            (string?)element.Attribute("ItemsSource") == "{Binding Usage.CompactWindows}"));

        var accountsVm = ReadSource(Path.Combine("src", "CodexSwitcher.App", "Features", "Accounts", "AccountsViewModel.cs"));
        Assert.Contains("public ObservableCollection<AccountItemViewModel> AllItems => _all;", accountsVm);
        Assert.Contains("SyncCollection(_all, targetItems);", accountsVm);
    }

    [Fact]
    public void TraySwitchesDispatchThroughExistingAccountAndProviderCommands()
    {
        var popup = ReadSource(Path.Combine("src", "CodexSwitcher.App", "Views", "TrayQuickControlPopup.xaml.cs"));
        var mainWindow = ReadSource(Path.Combine("src", "CodexSwitcher.App", "MainWindow.xaml.cs"));

        Assert.Contains("AccountSwitchRequested?.Invoke(account)", popup);
        Assert.Contains("ApiProviderSwitchRequested?.Invoke(provider)", popup);
        Assert.Contains("_trayQuickControlPopup.AccountSwitchRequested += SwitchAccountFromTrayPopup;", mainWindow);
        Assert.Contains("_trayQuickControlPopup.ApiProviderSwitchRequested += SwitchApiProviderFromTrayPopup;", mainWindow);

        var accountSwitch = GetMethodBody(mainWindow, "private async void SwitchAccountFromTrayPopup(");
        Assert.Contains("Root.ViewModel.Accounts.SwitchCommand.ExecuteAsync(account)", accountSwitch);

        var providerSwitch = GetMethodBody(mainWindow, "private async void SwitchApiProviderFromTrayPopup(");
        Assert.Contains("Root.ViewModel.ApiProviders.SwitchToApiProviderCommand.ExecuteAsync(provider)", providerSwitch);
    }

    [Fact]
    public void WindowCloseDisposesHotkeyServiceAndDisposeUnregistersAllBindings()
    {
        var mainWindow = ReadSource(Path.Combine("src", "CodexSwitcher.App", "MainWindow.xaml.cs"));
        var onWindowClosed = GetMethodBody(mainWindow, "private void OnWindowClosed(");
        Assert.Contains("_globalHotkeyService.Dispose();", onWindowClosed);

        var hotkeys = ReadSource(Path.Combine("src", "CodexSwitcher.App", "Services", "GlobalHotkeyService.cs"));
        var dispose = GetMethodBody(hotkeys, "public void Dispose()");
        Assert.Contains("UnregisterHotKey(_windowHandle, HotkeyId)", dispose);
        Assert.Contains("foreach (var id in _registeredQuickSwitchHotkeyIds)", dispose);
        Assert.Contains("UnregisterHotKey(_windowHandle, id)", dispose);
        Assert.Contains("RemoveWindowSubclass(_windowHandle, _subclassProcedure", dispose);
    }
}
