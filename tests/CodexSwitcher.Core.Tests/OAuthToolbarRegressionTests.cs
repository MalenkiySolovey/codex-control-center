using System.Xml.Linq;

namespace CodexSwitcher.Core.Tests;

public sealed class OAuthToolbarRegressionTests
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
                if (File.Exists(Path.Combine(directory.FullName, "src", "CodexSwitcher.App", "Views", "ShellView.xaml")))
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

    private static string? ButtonCommand(XElement button) =>
        (string?)button.Attribute("Command");

    private static string? ButtonLabel(XElement button) =>
        (string?)button.Descendants().FirstOrDefault(element => element.Name.LocalName == "TextBlock")?.Attribute("Text");

    [Fact]
    public void ToolbarShowsSeparateOAuthAndCodexSignInActions()
    {
        var shell = XDocument.Load(Path.Combine(RepositoryRoot, "src", "CodexSwitcher.App", "Views", "ShellView.xaml"));
        var toolbar = shell.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "AccountsToolbarPrimaryRow");
        var buttons = toolbar.Elements().Where(element => element.Name.LocalName == "Button").ToList();

        var oauth = Assert.Single(buttons, button => ButtonLabel(button) == "{x:Bind Loc.SignInOAuth}");
        var codexSignIn = Assert.Single(buttons, button => ButtonLabel(button) == "{x:Bind Loc.OpenCodexSignIn}");

        Assert.Equal(
            ["{x:Bind Loc.OpenCodexSignIn}", "{x:Bind Loc.SignInOAuth}"],
            buttons.Select(button => ButtonLabel(button)!).ToArray());

        var secondaryToolbar = shell.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "AccountsToolbarSecondaryRow");
        var secondaryButtons = secondaryToolbar.Elements()
            .Where(element => element.Name.LocalName == "Button")
            .ToList();
        Assert.Equal(
            [
                "{x:Bind Loc.RefreshButton}",
                "{x:Bind Loc.DetectAccount}",
                "{x:Bind Loc.ImportAccounts}",
                "{x:Bind Loc.Export}",
                "{x:Bind Loc.TwoFactorButton}",
                "{x:Bind Loc.SettingsButton}",
            ],
            secondaryButtons.Select(button => ButtonLabel(button)!).ToArray());

        Assert.Equal("{x:Bind ViewModel.Accounts.AddAccountCommand}", ButtonCommand(oauth));
        Assert.Equal("{x:Bind ViewModel.Accounts.OpenCodexSignInCommand}", ButtonCommand(codexSignIn));
        Assert.NotEqual(ButtonCommand(oauth), ButtonCommand(codexSignIn));
        Assert.Null(oauth.Attribute("Visibility"));
        Assert.Null(codexSignIn.Attribute("Visibility"));
        Assert.Null(codexSignIn.Attribute("Background"));
        Assert.Equal("{x:Bind Loc.SignInOAuthTooltip}", (string?)oauth.Attribute("ToolTipService.ToolTip"));
        Assert.Equal("{x:Bind Loc.OpenCodexSignInTooltip}", (string?)codexSignIn.Attribute("ToolTipService.ToolTip"));
        Assert.Equal("{ThemeResource BrandAccentBrush}", (string?)oauth.Attribute("Background"));

        var strings = ReadSource(Path.Combine("src", "CodexSwitcher.App", "Localization", "Strings.cs"));
        Assert.Contains("Add a ChatGPT account to Control Center using browser OAuth.", strings);
        Assert.Contains("without adding a Control Center account through OAuth.", strings);
    }

    [Fact]
    public void EveryVisibleOAuthEntryPointUsesTheSameAddAccountCommand()
    {
        var shell = XDocument.Load(Path.Combine(RepositoryRoot, "src", "CodexSwitcher.App", "Views", "ShellView.xaml"));
        var signInLabels = shell.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock" &&
                              (string?)element.Attribute("Text") == "{x:Bind Loc.SignInOAuth}")
            .ToList();

        Assert.Equal(3, signInLabels.Count); // toolbar plus both empty states
        foreach (var label in signInLabels)
        {
            var button = label.Ancestors().First(element => element.Name.LocalName == "Button");
            Assert.Equal("{x:Bind ViewModel.Accounts.AddAccountCommand}", ButtonCommand(button));
        }
    }

    [Fact]
    public void OAuthCommandUsesIsolatedLoginAndDoesNotTouchRoutingTarget()
    {
        var accountsVm = ReadSource(Path.Combine("src", "CodexSwitcher.App", "Features", "Accounts", "AccountsViewModel.cs"));
        var addAccount = GetMethodBody(accountsVm, "private async Task AddAccountAsync()");
        var accountDialog = ReadSource(Path.Combine("src", "CodexSwitcher.App", "Dialogs", "Accounts", "AccountDialogService.cs"));
        var loginWindow = ReadSource(Path.Combine("src", "CodexSwitcher.App", "Views", "LoginWindow.xaml.cs"));

        Assert.Contains("_ui.RunEphemeralLoginAsync()", addAccount);
        Assert.Contains("_credentials.AddFromAuthJson(authJson, null)", addAccount);
        Assert.DoesNotContain("_targetSwitchService", addAccount);
        Assert.DoesNotContain("TargetStateChanged", addAccount);
        Assert.DoesNotContain("ReturnToOpenAi", addAccount);
        Assert.DoesNotContain("OpenSignInAsync", addAccount);

        Assert.Contains("new Views.LoginWindow(_codex, _paths)", accountDialog);
        Assert.Contains("_userDataFolder = Path.Combine(paths.TempRoot, \"codex-webview-\"", loginWindow);
        Assert.Contains("_codexHome = Path.Combine(paths.TempRoot, \"codex-login-\"", loginWindow);
        Assert.Contains("StartChatGptLoginAsync(_codexHome, _cts.Token)", loginWindow);
        Assert.DoesNotContain("paths.Codex.Home", loginWindow);
    }

    [Fact]
    public void CancelledOAuthReturnsBeforeCreatingOrSavingAProfile()
    {
        var accountsVm = ReadSource(Path.Combine("src", "CodexSwitcher.App", "Features", "Accounts", "AccountsViewModel.cs"));
        var addAccount = GetMethodBody(accountsVm, "private async Task AddAccountAsync()");
        var loginWindow = ReadSource(Path.Combine("src", "CodexSwitcher.App", "Views", "LoginWindow.xaml.cs"));
        var onClosed = GetMethodBody(loginWindow, "private void OnClosed(");
        var cancelGuard = addAccount.IndexOf("if (authJson is null)", StringComparison.Ordinal);
        var saveProfile = addAccount.IndexOf("_credentials.AddFromAuthJson(authJson, null)", StringComparison.Ordinal);

        Assert.True(cancelGuard >= 0);
        Assert.True(saveProfile > cancelGuard);
        var cancellationPath = addAccount[cancelGuard..saveProfile];
        Assert.Contains("return;", cancellationPath);
        Assert.DoesNotContain("AddFromAuthJson", cancellationPath);
        Assert.Contains("private void OnCancelClick(object sender, RoutedEventArgs e) => Complete(null);", loginWindow);
        Assert.Contains("_cts.Cancel()", onClosed);
        Assert.Contains("_tcs.TrySetResult(null)", onClosed);
        Assert.Contains("CleanupTempAsync()", onClosed);
    }

    [Fact]
    public void OpenCodexSignInUsesItsDedicatedConfirmedWorkflowNotOAuth()
    {
        var accountsVm = ReadSource(Path.Combine("src", "CodexSwitcher.App", "Features", "Accounts", "AccountsViewModel.cs"));
        var openCodexSignIn = GetMethodBody(accountsVm, "private async Task OpenCodexSignInAsync()");

        Assert.Contains("_ui.ConfirmAsync(", openCodexSignIn);
        Assert.Contains("_signedOutService.OpenSignInAsync()", openCodexSignIn);
        Assert.DoesNotContain("AddAccountAsync", openCodexSignIn);
        Assert.DoesNotContain("RunEphemeralLoginAsync", openCodexSignIn);
    }
}
