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
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Collections.Specialized;
using System.ComponentModel;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.Dialogs;
using CodexSwitcher.App.Shell.Theme;
using CodexSwitcher.App.Shell.Windowing;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Views;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace CodexSwitcher.App;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private const int SW_RESTORE = 9;

    private readonly AppWindow _appWindow;
    private readonly WindowChromeService _chromeService;
    private readonly WindowLifecycleCoordinator _lifecycleCoordinator;
    private readonly IProcessManager _processManager;
    private readonly IGlobalHotkeyService _globalHotkeyService;
    private readonly ICommonDialogService _dialogs;
    private readonly AppSettings _settings;
    private readonly MenuFlyout _trayMenu = new();
    private readonly HashSet<CodexSwitcher.App.ViewModels.AccountItemViewModel> _trayObservedAccounts = [];
    private readonly HashSet<CodexSwitcher.App.ViewModels.AccountUsageViewModel> _trayObservedUsage = [];
    private TrayQuickControlPopup? _trayQuickControlPopup;
    private QuickSwitchOverlayWindow? _quickSwitchOverlayWindow;
    private bool _exitRequestedFromTray;

    public CodexSwitcher.App.Views.ShellView ShellRoot => Root;

    public MainWindow(
        WindowChromeService chromeService,
        WindowLifecycleCoordinator lifecycleCoordinator,
        IThemeService themeService,
        WindowHandleProvider handleProvider,
        CodexSwitcher.App.Dialogs.Shared.IDialogHost dialogHost,
        CodexSwitcher.App.Shell.ShellViewModel shellViewModel,
        IProcessManager processManager,
        IGlobalHotkeyService globalHotkeyService,
        ICommonDialogService dialogs,
        AppSettings settings,
        CodexSwitcher.Infra.Common.Paths.AppPaths paths,
        Func<CodexSwitcher.App.Features.Settings.SettingsViewModel> settingsVmFactory)
    {
        StartupTracer.Instance.RecordMilestone("T7b:MainWindowXamlInitializationStarted");
        InitializeComponent();
        StartupTracer.Instance.RecordMilestone("T7c:MainWindowXamlInitializationCompleted");

        _chromeService = chromeService ?? throw new ArgumentNullException(nameof(chromeService));
        _lifecycleCoordinator = lifecycleCoordinator ?? throw new ArgumentNullException(nameof(lifecycleCoordinator));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _globalHotkeyService = globalHotkeyService ?? throw new ArgumentNullException(nameof(globalHotkeyService));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        Root.Initialize(shellViewModel, settingsVmFactory);
        StartupTracer.Instance.RecordMilestone("T8:ShellResolved");

        _chromeService.ConfigureTitleBar(this, Root.TitleBarElement);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _chromeService.TrySetWindowIcon(this, _appWindow);
        InitializeTrayIcon();
        try
        {
            _globalHotkeyService.AttachToWindow(
                hwnd,
                DispatcherQueue,
                BringToFront,
                SwitchFromQuickSwitchHotkey,
                ShowQuickSwitchOverlay);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] Registration unavailable: {ex.GetType().Name}");
        }

        _appWindow.Changed += OnAppWindowChanged;
        _appWindow.Closing += OnAppWindowClosing;
        VisibilityChanged += OnVisibilityChanged;
        Activated += OnWindowActivated;

        if (Content is FrameworkElement rootVisual)
        {
            themeService.Initialize(rootVisual);
            if (_trayQuickControlPopup is not null)
            {
                _trayQuickControlPopup.RequestedTheme = rootVisual.ActualTheme;
                rootVisual.ActualThemeChanged += (_, _) =>
                {
                    if (_trayQuickControlPopup is not null)
                    {
                        _trayQuickControlPopup.RequestedTheme = rootVisual.ActualTheme;
                    }
                };
            }
        }

        handleProvider.MainWindowHandle = hwnd;
        dialogHost.Attach(this);

        Closed += OnWindowClosed;
    }

    private int _activatedOnce;
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (Interlocked.Exchange(ref _activatedOnce, 1) == 0)
        {
            StartupTracer.Instance.RecordMilestone("T10:FirstWindowActivated");
        }
        _lifecycleCoordinator.HandleActivation(args.WindowActivationState, () => Root.ViewModel?.HideAllRevealedTotp());
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_exitRequestedFromTray)
        {
            args.Cancel = true;
            HideToTray();
            ShutdownTracer.Instance.RecordMilestone("S15:WindowHidden");
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _quickSwitchOverlayWindow?.Close();
        _quickSwitchOverlayWindow = null;
        Root.TrayTooltipSettingsChanged -= RefreshTrayTooltip;
        Root.ViewModel.Routing.PropertyChanged -= OnTrayRoutingPropertyChanged;
        Root.ViewModel.Accounts.AllItems.CollectionChanged -= OnTrayAccountsCollectionChanged;
        DetachTrayAccountObservers();
        _globalHotkeyService.Dispose();
        TrayIcon?.Dispose();
        _lifecycleCoordinator.HandleWindowClosed(
            () => Root.ViewModel?.HideAllRevealedTotp(),
            () => Root.ViewModel?.Cleanup());
    }

    private void InitializeTrayIcon()
    {
        _trayMenu.Opening += (_, _) => RebuildTrayMenu();
        TrayIcon.ContextFlyout = _trayMenu;
        Root.TrayTooltipSettingsChanged += RefreshTrayTooltip;
        Root.ViewModel.Routing.PropertyChanged += OnTrayRoutingPropertyChanged;
        Root.ViewModel.Accounts.AllItems.CollectionChanged += OnTrayAccountsCollectionChanged;
        AttachTrayAccountObservers();
        RefreshTrayTooltip();

        _trayQuickControlPopup = new TrayQuickControlPopup
        {
            DataContext = Root.ViewModel,
            RequestedTheme = Root.ActualTheme,
        };
        _trayQuickControlPopup.OpenSwitchboardRequested += OpenSwitchboardFromTrayPopup;
        _trayQuickControlPopup.RefreshAllRequested += RefreshAllFromTrayPopup;
        _trayQuickControlPopup.OpenCodexRequested += OpenCodexFromTrayPopup;
        _trayQuickControlPopup.AccountSwitchRequested += SwitchAccountFromTrayPopup;
        _trayQuickControlPopup.ApiProviderSwitchRequested += SwitchApiProviderFromTrayPopup;
        _trayQuickControlPopup.LaunchProfileToggleRequested += ToggleLaunchProfileFromTrayPopup;
        _trayQuickControlPopup.ManageLaunchProfilesRequested += OpenLaunchProfilesFromTrayPopup;
        _trayQuickControlPopup.OpenSettingsRequested += OpenSettingsFromTrayPopup;
        _trayQuickControlPopup.QuitRequested += ExitFromTray;
        TrayIcon.TrayPopup = _trayQuickControlPopup;

        // H.NotifyIcon invokes LeftClickCommand before displaying TrayPopup. Leaving it unset
        // keeps a left click useful while Switchboard is hidden instead of restoring the window.
        TrayIcon.DoubleClickCommand = new RelayCommand(() =>
        {
            TrayIcon.CloseTrayPopup();
            BringToFront();
        });
    }

    private void OnTrayRoutingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
            or nameof(CodexSwitcher.App.Shell.Routing.ActiveTargetPresentationCoordinator.ActiveTargetSummary)
            or nameof(CodexSwitcher.App.Shell.Routing.ActiveTargetPresentationCoordinator.IsRoutingActiveToApi))
        {
            RefreshTrayTooltip();
        }
    }

    private void OnTrayAccountsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        AttachTrayAccountObservers();
        RefreshTrayTooltip();
    }

    private void AttachTrayAccountObservers()
    {
        DetachTrayAccountObservers();
        foreach (var account in Root.ViewModel.Accounts.AllItems)
        {
            account.PropertyChanged += OnTrayAccountPropertyChanged;
            _trayObservedAccounts.Add(account);
            account.Usage.PropertyChanged += OnTrayUsagePropertyChanged;
            _trayObservedUsage.Add(account.Usage);
        }
    }

    private void DetachTrayAccountObservers()
    {
        foreach (var account in _trayObservedAccounts)
        {
            account.PropertyChanged -= OnTrayAccountPropertyChanged;
        }
        _trayObservedAccounts.Clear();

        foreach (var usage in _trayObservedUsage)
        {
            usage.PropertyChanged -= OnTrayUsagePropertyChanged;
        }
        _trayObservedUsage.Clear();
    }

    private void OnTrayAccountPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshTrayTooltip();

    private void OnTrayUsagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
            or nameof(CodexSwitcher.App.ViewModels.AccountUsageViewModel.CompactStatusText)
            or nameof(CodexSwitcher.App.ViewModels.AccountUsageViewModel.ShowStaleBadge)
            or nameof(CodexSwitcher.App.ViewModels.AccountUsageViewModel.IsStale))
        {
            RefreshTrayTooltip();
        }
    }

    private void RefreshTrayTooltip()
    {
        if (TrayIcon is null)
        {
            return;
        }

        var loc = Strings.Current;
        var targetSummary = GetTrayActiveTargetSummary(loc);
        var tooltip = _settings.TrayTooltipMode switch
        {
            TrayTooltipMode.ActiveTarget => string.Concat(loc.TrayTooltip, " · ", targetSummary),
            TrayTooltipMode.ActiveTargetAndCachedQuota =>
                string.Concat(loc.TrayTooltip, " · ", targetSummary, " · ", GetTrayQuotaSummary()),
            _ => loc.TrayTooltip,
        };

        const int maxTooltipLength = 127;
        TrayIcon.ToolTipText = tooltip.Length <= maxTooltipLength
            ? tooltip
            : string.Concat(tooltip[..(maxTooltipLength - 1)], "…");
    }

    private string GetTrayActiveTargetSummary(Strings loc)
    {
        var activeTarget = Root.ViewModel.Routing.ActiveTargetSummary;
        return string.IsNullOrWhiteSpace(activeTarget) ? loc.NoManaged : activeTarget;
    }

    private void RebuildTrayMenu()
    {
        _trayMenu.Items.Clear();
        AddTrayItem(Strings.Current.TrayOpenSwitchboard, BringToFront);
        AddTrayItem(Strings.Current.TrayRefreshAll, () => Root.ViewModel.Accounts.RefreshAllCommand.Execute(null));
        AddTrayItem(Strings.Current.TrayOpenCodex, OpenCodexFromTrayPopup);

        _trayMenu.Items.Add(new MenuFlyoutSeparator());
        AddTrayHeading(Strings.Current.TrayActiveTarget);
        var activeTarget = Root.ViewModel.Routing.ActiveTargetSummary;
        AddTrayItem(string.IsNullOrWhiteSpace(activeTarget) ? Strings.Current.NoManaged : activeTarget, static () => { }, enabled: false);
        AddTrayHeading(Strings.Current.TrayCachedQuota);
        AddTrayItem(GetTrayQuotaSummary(), static () => { }, enabled: false);

        _trayMenu.Items.Add(new MenuFlyoutSeparator());
        AddTrayItem(Strings.Current.TrayChatGptAccounts, () => OpenTabFromTray(0));
        AddTrayItem(Strings.Current.TrayApiProviders, () => OpenTabFromTray(1));
        AddTrayItem(Strings.Current.TrayManageLaunchProfiles, () => OpenTabFromTray(2));

        _trayMenu.Items.Add(new MenuFlyoutSeparator());
        AddTrayItem(Strings.Current.TrayOpenSettings, () =>
        {
            BringToFront();
            Root.OpenSettingsFromTray();
        });
        AddTrayItem(Strings.Current.TrayQuit, ExitFromTray);
    }

    private void OpenSwitchboardFromTrayPopup()
    {
        TrayIcon.CloseTrayPopup();
        BringToFront();
    }

    private async void RefreshAllFromTrayPopup()
    {
        await RunTrayActionFromPopupAsync(
            () => Root.ViewModel.Accounts.RefreshAllCommand.ExecuteAsync(null));
    }

    private void OpenCodexFromTrayPopup()
    {
        TrayIcon.CloseTrayPopup();
        if (!_processManager.TryLaunchDesktop())
        {
            BringToFront();
        }
    }

    private async void SwitchAccountFromTrayPopup(ViewModels.AccountItemViewModel account)
    {
        await RunTrayActionFromPopupAsync(
            () => Root.ViewModel.Accounts.SwitchCommand.ExecuteAsync(account),
            requiresSwitchConfirmation: true);
    }

    private async void SwitchApiProviderFromTrayPopup(ViewModels.ApiProviderItemViewModel provider)
    {
        await RunTrayActionFromPopupAsync(
            () => Root.ViewModel.ApiProviders.SwitchToApiProviderCommand.ExecuteAsync(provider),
            requiresSwitchConfirmation: true);
    }

    private async Task RunTrayActionFromPopupAsync(
        Func<Task> action,
        bool requiresSwitchConfirmation = false,
        IntPtr restoreForegroundWindow = default)
    {
        TrayIcon.CloseTrayPopup();
        if (requiresSwitchConfirmation && _settings.AlwaysConfirmSwitch)
        {
            BringToFront();
        }

        var actionFailed = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            actionFailed = true;
            Root.ViewModel.Notifications.ShowError(Strings.Current.ErrorTitle, ex.Message);
        }

        var notifications = Root.ViewModel.Notifications;
        if (!Visible && notifications.IsOpen &&
            notifications.Severity is InfoBarSeverity.Warning or InfoBarSeverity.Error)
        {
            BringToFront();
        }
        else if (!actionFailed && restoreForegroundWindow != IntPtr.Zero &&
                 !(requiresSwitchConfirmation && _settings.AlwaysConfirmSwitch))
        {
            SetForegroundWindow(restoreForegroundWindow);
        }
    }

    private void ShowQuickSwitchOverlay()
    {
        var foregroundWindow = GetForegroundWindow();
        var foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
        var isCodexDesktopForeground = foregroundThreadId != 0 && IsCodexDesktopProcess(foregroundProcessId);
        if (!isCodexDesktopForeground)
        {
            BringToFront();
            Root.ViewModel.Notifications.ShowInformation(
                Strings.Current.QuickSwitchOverlayRequiresCodexTitle,
                Strings.Current.QuickSwitchOverlayRequiresCodexMessage);
            return;
        }

        _quickSwitchOverlayWindow?.Close();
        var overlay = new QuickSwitchOverlayWindow(Root.ViewModel, Root.ActualTheme, foregroundWindow);
        _quickSwitchOverlayWindow = overlay;
        overlay.AccountSwitchRequested += account => _ = RunTrayActionFromPopupAsync(
            () => Root.ViewModel.Accounts.SwitchCommand.ExecuteAsync(account),
            requiresSwitchConfirmation: true,
            restoreForegroundWindow: foregroundWindow);
        overlay.ApiProviderSwitchRequested += provider => _ = RunTrayActionFromPopupAsync(
            () => Root.ViewModel.ApiProviders.SwitchToApiProviderCommand.ExecuteAsync(provider),
            requiresSwitchConfirmation: true,
            restoreForegroundWindow: foregroundWindow);
        overlay.Closed += (_, _) =>
        {
            if (ReferenceEquals(_quickSwitchOverlayWindow, overlay))
            {
                _quickSwitchOverlayWindow = null;
            }
        };
        overlay.Activate();
    }

    private static bool IsCodexDesktopProcess(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!string.Equals(process.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            var normalizedPath = executablePath.Replace('/', '\\').ToLowerInvariant();
            return normalizedPath.Contains("openai.codex", StringComparison.Ordinal) ||
                   normalizedPath.Contains("\\windowsapps\\openai", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private void SwitchFromQuickSwitchHotkey(int slotNumber)
    {
        var assignment = (_settings.QuickSwitchHotkeyAssignments ?? [])
            .FirstOrDefault(item => item.SlotNumber == slotNumber);
        if (assignment?.TargetKind is not { } targetKind || assignment.TargetId is not { } targetId)
        {
            return;
        }

        BringToFront();
        if (targetKind == QuickSwitchTargetKind.Account)
        {
            var account = Root.ViewModel.Accounts.AllItems.FirstOrDefault(item => item.Id == targetId);
            var loc = Strings.Current;
            if (account is null)
            {
                Root.ViewModel.Notifications.ShowWarning(loc.QuickSwitchTargetUnavailableTitle, loc.QuickSwitchAccountMissingMessage);
            }
            else if (account.IsRoutingActive)
            {
                Root.ViewModel.Notifications.ShowInformation(
                    loc.QuickSwitchAlreadyActiveTitle,
                    loc.QuickSwitchAccountAlreadyActiveMessage(account.DisplayName));
            }
            else if (!account.CanSwitch)
            {
                Root.ViewModel.Notifications.ShowWarning(
                    loc.QuickSwitchAccountUnavailableTitle,
                    loc.QuickSwitchAccountUnavailableMessage);
            }
            else
            {
                Root.ViewModel.Accounts.SwitchCommand.Execute(account);
            }
            return;
        }

        if (targetKind == QuickSwitchTargetKind.ApiProvider)
        {
            var provider = Root.ViewModel.ApiProviders.Items.FirstOrDefault(item => item.Id == targetId);
            var loc = Strings.Current;
            if (provider is null)
            {
                Root.ViewModel.Notifications.ShowWarning(loc.QuickSwitchTargetUnavailableTitle, loc.QuickSwitchProviderMissingMessage);
            }
            else if (provider.IsTargetActive)
            {
                Root.ViewModel.Notifications.ShowInformation(
                    loc.QuickSwitchAlreadyActiveTitle,
                    loc.QuickSwitchProviderAlreadyActiveMessage(provider.DisplayName));
            }
            else if (!provider.CanSwitch)
            {
                Root.ViewModel.Notifications.ShowWarning(
                    loc.QuickSwitchProviderUnavailableTitle,
                    loc.QuickSwitchProviderMissingKeyMessage);
            }
            else
            {
                Root.ViewModel.ApiProviders.SwitchToApiProviderCommand.Execute(provider);
            }
        }
    }

    private async void ToggleLaunchProfileFromTrayPopup(Features.Launch.CodexLaunchProfileItemViewModel profile)
    {
        if (profile.IsRunning)
        {
            TrayIcon.CloseTrayPopup();
            BringToFront();
        }

        await RunTrayActionFromPopupAsync(
            () => Root.ViewModel.LaunchProfiles.ToggleProfileCommand.ExecuteAsync(profile));
    }

    private void OpenLaunchProfilesFromTrayPopup() => OpenTabFromTray(2);

    private void OpenSettingsFromTrayPopup()
    {
        TrayIcon.CloseTrayPopup();
        BringToFront();
        Root.OpenSettingsFromTray();
    }

    private void OpenTabFromTray(int tab)
    {
        TrayIcon.CloseTrayPopup();
        BringToFront();
        Root.ViewModel.SelectedTab = tab;
    }

    private string GetTrayQuotaSummary()
    {
        var loc = Strings.Current;
        if (Root.ViewModel.Routing.IsRoutingActiveToApi)
        {
            return loc.TrayQuotaNotApplicable;
        }

        var activeAccount = Root.ViewModel.Accounts.AllItems.FirstOrDefault(account => account.IsRoutingActive);
        if (activeAccount is null)
        {
            return loc.TrayNoQuotaData;
        }

        var windows = activeAccount.Usage.CompactWindows;
        var summary = windows.Count == 0
            ? activeAccount.Usage.CompactStatusText
            : string.Join(" · ", windows.Take(2).Select(window => $"{window.DisplayLabel}: {window.CompactRemainingText}"));

        return windows.Count > 0 && activeAccount.Usage.ShowStaleBadge
            ? $"{summary} · {loc.TrayStaleQuota}"
            : summary;
    }

    private void AddTrayHeading(string text)
    {
        _trayMenu.Items.Add(new MenuFlyoutItem { Text = text, IsEnabled = false });
    }

    private void AddTrayItem(string text, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        item.Click += (_, _) => action();
        _trayMenu.Items.Add(item);
    }

    private async void ExitFromTray()
    {
        try
        {
            var sessionCount = Root.ViewModel.LaunchProfiles.RunningSessions.Count;
            if (sessionCount > 0)
            {
                TrayIcon.CloseTrayPopup();
                BringToFront();
                var loc = Strings.Current;
                var confirmed = await _dialogs.ConfirmAsync(
                    loc.LaunchQuitConfirmTitle,
                    loc.LaunchQuitConfirmMessage(sessionCount),
                    loc.LaunchQuitConfirmButton,
                    destructive: true);
                if (!confirmed)
                {
                    return;
                }
            }

            _exitRequestedFromTray = true;
            Close();
        }
        catch (Exception ex)
        {
            Root.ViewModel.Notifications.ShowError(Strings.Current.ErrorTitle, ex.Message);
            BringToFront();
        }
    }

    public void BringToFront()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    public void HideToTray()
    {
        try { _appWindow.Hide(); }
        catch { }
        Root.ViewModel?.HideAllRevealedTotp();
        UpdateForegroundState();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange || args.DidSizeChange || args.DidVisibilityChange)
        {
            UpdateForegroundState();
        }
    }

    private void OnVisibilityChanged(object sender, WindowVisibilityChangedEventArgs args)
    {
        if (args.Visible)
        {
            StartupTracer.Instance.RecordMilestone("T10a:FirstWindowVisible");
        }
        UpdateForegroundState();
    }

    private void UpdateForegroundState()
    {
        bool isVisible = Visible;
        bool isMinimized = false;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            isMinimized = presenter.State == OverlappedPresenterState.Minimized;
        }

        _lifecycleCoordinator.HandleForegroundChange(
            isVisible,
            isMinimized,
            active => Root.ViewModel.SetForegroundActive(active),
            () => Root.ViewModel.HideAllRevealedTotp());
    }
}
