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
using System.Linq;
using CodexSwitcher.App.Features.Accounts;
using CodexSwitcher.App.Features.Providers;
using CodexSwitcher.App.Features.Launch;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.Shell;
using CodexSwitcher.App.Features.Settings;
using CodexSwitcher.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace CodexSwitcher.App.Views;

public sealed partial class ShellView : UserControl
{
    private const double MinimumShellZoom = 0.75;
    private const double MaximumShellZoom = 1.5;
    private const double ShellZoomPerWheelNotch = 1.1;
    private Func<SettingsViewModel>? _settingsVmFactory;
    private Action? _pendingApiProviderMenuAction;
    private Action? _pendingAccountMenuAction;
    private double _shellZoomFactor = 1;

    public ShellViewModel ViewModel { get; private set; } = null!;
    public Strings Loc => Strings.Current;
    public event Action? TrayTooltipSettingsChanged;

    /// <summary>Elemento usado como região de arraste da barra de título (Mica).</summary>
    public UIElement TitleBarElement => AppTitleBar;

    public ShellView()
    {
        InitializeComponent();
        AccountsHeaderGrid.SizeChanged += OnAccountsHeaderSizeChanged;
        ApiProvidersHeaderGrid.SizeChanged += OnSecondaryHeaderSizeChanged;
        LaunchProfilesHeaderGrid.SizeChanged += OnSecondaryHeaderSizeChanged;
        TrySetTitleBarIcon();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnShellZoomViewboxSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateShellZoomLayout(e.NewSize.Width, e.NewSize.Height);

    private void OnShellPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if ((e.KeyModifiers & VirtualKeyModifiers.Control) == 0)
        {
            return;
        }

        var wheelDelta = e.GetCurrentPoint(ShellZoomViewbox).Properties.MouseWheelDelta;
        if (wheelDelta == 0)
        {
            return;
        }

        _shellZoomFactor = Math.Clamp(
            _shellZoomFactor * Math.Pow(ShellZoomPerWheelNotch, wheelDelta / 120d),
            MinimumShellZoom,
            MaximumShellZoom);
        UpdateShellZoomLayout(ShellZoomViewbox.ActualWidth, ShellZoomViewbox.ActualHeight);
        e.Handled = true;
    }

    private void UpdateShellZoomLayout(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        // Size the logical layout inversely to the Viewbox scale so the full
        // window remains filled while all controls, text, and spacing zoom as
        // one coherent surface. The existing inner ScrollViewers keep their
        // own scrolling behavior at the resulting logical viewport size.
        ShellRootGrid.Width = viewportWidth / _shellZoomFactor;
        ShellRootGrid.Height = viewportHeight / _shellZoomFactor;
    }

    private bool _isAccountsHeaderCompact;
    private bool _isSearchInSecondaryToolbarRow;

    private void OnAccountsHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double compactLayoutBreakpoint = 1040;
        var useCompactLayout = e.NewSize.Width < compactLayoutBreakpoint;
        var useSecondarySearchRow = e.NewSize.Width < 860;
        SearchBox.Width = useSecondarySearchRow
            ? Math.Max(220, e.NewSize.Width - 690)
            : e.NewSize.Width < 1200
                ? 220
                : 260;

        if (_isSearchInSecondaryToolbarRow != useSecondarySearchRow)
        {
            if (useSecondarySearchRow)
            {
                AccountsToolbarPrimaryRow.Children.Remove(SearchBox);
                AccountsToolbarSecondaryRow.Children.Add(SearchBox);
            }
            else
            {
                AccountsToolbarSecondaryRow.Children.Remove(SearchBox);
                AccountsToolbarPrimaryRow.Children.Insert(0, SearchBox);
            }

            _isSearchInSecondaryToolbarRow = useSecondarySearchRow;
        }

        if (_isAccountsHeaderCompact == useCompactLayout)
        {
            return;
        }

        _isAccountsHeaderCompact = useCompactLayout;
        AccountsHeaderGrid.ColumnDefinitions[0].Width = useCompactLayout
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;
        AccountsHeaderGrid.ColumnDefinitions[1].Width = useCompactLayout
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(AccountsToolbar, useCompactLayout ? 0 : 1);
        Grid.SetRow(AccountsToolbar, useCompactLayout ? 1 : 0);
        AccountsToolbar.HorizontalAlignment = useCompactLayout ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        AccountsToolbar.Margin = useCompactLayout ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        AccountsToolbarPrimaryRow.HorizontalAlignment = useCompactLayout ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        AccountsToolbarSecondaryRow.HorizontalAlignment = useCompactLayout ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private void OnSecondaryHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid headerGrid)
        {
            return;
        }

        var toolbar = ReferenceEquals(headerGrid, ApiProvidersHeaderGrid)
            ? ApiProvidersToolbar
            : LaunchProfilesToolbar;
        var useCompactLayout = e.NewSize.Width < 1040;
        headerGrid.ColumnDefinitions[0].Width = useCompactLayout
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;
        headerGrid.ColumnDefinitions[1].Width = useCompactLayout
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(toolbar, useCompactLayout ? 0 : 1);
        Grid.SetRow(toolbar, useCompactLayout ? 1 : 0);
        toolbar.HorizontalAlignment = useCompactLayout ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        toolbar.Margin = useCompactLayout ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }

    public void Initialize(ShellViewModel viewModel, Func<SettingsViewModel> settingsVmFactory)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _settingsVmFactory = settingsVmFactory ?? throw new ArgumentNullException(nameof(settingsVmFactory));
    }

    private void TrySetTitleBarIcon()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (System.IO.File.Exists(iconPath))
                AppIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
        }
        catch (Exception)
        {
            // Ícone é cosmético.
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupTracer.Instance.RecordMilestone("T11:ShellLoaded");
        Loaded -= OnLoaded;
        if (ViewModel is not null)
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            CompositionTarget.Rendering += OnFirstFrameRendered;
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => CompositionTarget.Rendering += OnFirstFrameRendered);
        }
    }

    private int _firstFrameScheduled;
    private int _isUnloaded;

    private void OnFirstFrameRendered(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnFirstFrameRendered;
        if (Interlocked.Exchange(ref _firstFrameScheduled, 1) != 0)
        {
            return;
        }

        StartupTracer.Instance.RecordMilestone("T13b:CachedContentFirstRender");
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (Volatile.Read(ref _isUnloaded) != 0)
            {
                return;
            }

            StartupTracer.Instance.RecordMilestone("T12:UIInteractive");
            ViewModel.Accounts.StartPostInteractiveInitialization();
            StartupTracer.Instance.FlushToFile();
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Interlocked.Exchange(ref _isUnloaded, 1);
        CompositionTarget.Rendering -= OnFirstFrameRendered;
    }

    private TotpWindow? _totpWindow;

    private void OnOpen2FaClick(object sender, RoutedEventArgs e)
    {
        // Reaproveita a janelinha se já estiver aberta (evita duplicatas).
        if (_totpWindow is not null)
        {
            _totpWindow.Activate();
            return;
        }

        _totpWindow = new TotpWindow();
        _totpWindow.Closed += (_, _) => _totpWindow = null;
        _totpWindow.Activate();
    }

    private SettingsWindow? _settingsWindow;
    private int _settingsOpenTraceSequence;

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsFromTray();
    }

    public void OpenSettingsFromTray()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var tracePrefix = $"S{System.Threading.Interlocked.Increment(ref _settingsOpenTraceSequence):D3}:SettingsOpen";
        StartupTracer.Instance.RecordMilestone($"{tracePrefix}:Click");

        var factory = _settingsVmFactory ?? throw new InvalidOperationException("SettingsViewModel factory não inicializada.");
        var vm = factory();
        vm.TrayTooltipPresentationChanged += OnTrayTooltipPresentationChanged;
        StartupTracer.Instance.RecordMilestone($"{tracePrefix}:ViewModelCreated");

        vm.OnMigrationCompleted = () =>
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (ViewModel is not null)
                {
                    await ViewModel.LoadCommand.ExecuteAsync(null);
                }
            });
        };

        _settingsWindow = new SettingsWindow(vm, tracePrefix);
        _settingsWindow.Closed += (_, _) =>
        {
            vm.TrayTooltipPresentationChanged -= OnTrayTooltipPresentationChanged;
            _settingsWindow = null;
        };
        _settingsWindow.Activate();
    }

    private void OnTrayTooltipPresentationChanged() => TrayTooltipSettingsChanged?.Invoke();

    private static AccountItemViewModel? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as AccountItemViewModel;

    private void QueueAccountMenuAction(object sender, Action<AccountItemViewModel> action)
    {
        if (ItemOf(sender) is { } item)
        {
            _pendingAccountMenuAction = () => action(item);
        }
    }

    private void OnAccountActionFlyoutClosed(object sender, object e)
    {
        var action = _pendingAccountMenuAction;
        _pendingAccountMenuAction = null;
        if (action is not null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => action());
        }
    }

    private void OnMoveUpAccountClick(object sender, RoutedEventArgs e) =>
        QueueAccountMenuAction(sender, item => ViewModel.Accounts.MoveUpCommand.Execute(item));

    private void OnMoveDownAccountClick(object sender, RoutedEventArgs e) =>
        QueueAccountMenuAction(sender, item => ViewModel.Accounts.MoveDownCommand.Execute(item));

    private void OnSwitchClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.SwitchCommand.Execute(item);
    }

    private void OnRefreshAccountClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.RefreshAccountCommand.Execute(item);
    }

    private void OnRevealTotpClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.RevealTotpCommand.Execute(item);
    }

    private void OnHideTotpClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.HideTotpCommand.Execute(item);
    }

    private void OnCopyTotpClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.CopyTotpCodeCommand.Execute(item);
    }

    private void OnAddOrManageTotpClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.AddOrManageTotpCommand.Execute(item);
    }

    private void OnToggleCollapseClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ViewModel.Accounts.ToggleAccountCollapseCommand.Execute(item);
            sw.Stop();
            System.Diagnostics.Debug.WriteLine($"[PerfDiag] Account toggle collapse (Id={item.Id}, IsCompact={item.IsCompact}) took {sw.ElapsedMilliseconds}ms ({sw.ElapsedTicks} ticks)");
        }
    }

    private void OnExportItemClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.ExportOneCommand.Execute(item);
    }

    private void OnRenameItemClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.RenameCommand.Execute(item);
    }

    private void OnSubscriptionTrackingClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.EditSubscriptionTrackingCommand.Execute(item);
    }

    private void OnRemoveItemClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.RemoveCommand.Execute(item);
    }

    private void OnReauthenticateClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.ReauthenticateCommand.Execute(item);
    }

    private void OnMarkReLoginClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.MarkNeedsReLoginCommand.Execute(item);
    }

    private void OnMarkUsedClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.MarkUsedCommand.Execute(item);
    }

    private void OnUnmarkUsedClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.UnmarkUsedCommand.Execute(item);
    }

    private void OnAccountsReordered(object sender, DragItemsCompletedEventArgs e)
    {
        if (e.DropResult != DataPackageOperation.Move) return;
        var orderedIds = ViewModel.Accounts.Items.Select(a => a.Id).ToList();
        ViewModel.Accounts.ReorderCommand.Execute(orderedIds);
    }

    private static ApiProviderItemViewModel? ApiItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ApiProviderItemViewModel;

    private static CodexLaunchProfileItemViewModel? LaunchProfileItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as CodexLaunchProfileItemViewModel;

    private static CodexLaunchSession? LaunchSessionOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as CodexLaunchSession;

    private void OnToggleLaunchProfileClick(object sender, RoutedEventArgs e)
    {
        if (LaunchProfileItemOf(sender) is { } item)
            ViewModel.LaunchProfiles.ToggleProfileCommand.Execute(item);
    }

    private void OnEditLaunchProfileClick(object sender, RoutedEventArgs e)
    {
        if (LaunchProfileItemOf(sender) is { } item)
            ViewModel.LaunchProfiles.EditCommand.Execute(item);
    }

    private void OnDeleteLaunchProfileClick(object sender, RoutedEventArgs e)
    {
        if (LaunchProfileItemOf(sender) is { } item)
            ViewModel.LaunchProfiles.DeleteCommand.Execute(item);
    }

    private void OnStopLaunchSessionClick(object sender, RoutedEventArgs e)
    {
        if (LaunchSessionOf(sender) is { } session)
            ViewModel.LaunchProfiles.StopSessionCommand.Execute(session);
    }

    private void OnSwitchApiClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.SwitchToApiProviderCommand.Execute(item);
    }

    private void OnToggleRouteClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.ToggleRouteCommand.Execute(item);
    }

    private void OnContinueOnClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.ContinueOnCommand.Execute(item);
    }

    private void OnDiscoverModelsClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.DiscoverModelsCommand.Execute(item);
    }

    private void QueueProviderMenuAction(object sender, Action<ApiProviderItemViewModel> action)
    {
        if (ApiItemOf(sender) is not { } item)
        {
            return;
        }

        // MenuFlyoutItem.Click runs while its PopupHost is still active. Wait for
        // the owning flyout's Closed event before opening windows or changing the
        // collection whose template created that popup.
        _pendingApiProviderMenuAction = () => action(item);
    }

    private void OnApiProviderActionFlyoutClosed(object sender, object e)
    {
        var action = _pendingApiProviderMenuAction;
        _pendingApiProviderMenuAction = null;
        if (action is not null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => action());
        }
    }

    private void OnEditApiProviderClick(object sender, RoutedEventArgs e)
    {
        QueueProviderMenuAction(sender, item => ViewModel.ApiProviders.EditApiProviderCommand.Execute(item));
    }

    private void OnRefreshProviderBillingClick(object sender, RoutedEventArgs e)
    {
        QueueProviderMenuAction(sender, item => ViewModel.ApiProviders.RefreshBillingCommand.Execute(item));
    }

    private void OnCloneApiProviderClick(object sender, RoutedEventArgs e)
    {
        QueueProviderMenuAction(sender, item => ViewModel.ApiProviders.CloneProfileCommand.Execute(item));
    }

    private void OnCloneWithNewKeyClick(object sender, RoutedEventArgs e)
    {
        QueueProviderMenuAction(sender, item => ViewModel.ApiProviders.CloneWithNewKeyCommand.Execute(item));
    }

    private void OnAddAnotherModelClick(object sender, RoutedEventArgs e)
    {
        QueueProviderMenuAction(sender, item => ViewModel.ApiProviders.AddAnotherModelCommand.Execute(item));
    }

    private void OnRetestCompatibilityClick(object sender, RoutedEventArgs e)
    {
        QueueProviderMenuAction(sender, item => ViewModel.ApiProviders.RetestCompatibilityCommand.Execute(item));
    }

    private void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        QueueProviderMenuAction(sender, item => ViewModel.ApiProviders.ExportDiagnosticsCommand.Execute(item));
    }

    private void OnRotateApiKeyClick(object sender, RoutedEventArgs e)
    {
        QueueProviderMenuAction(sender, item => ViewModel.ApiProviders.RotateApiKeyCommand.Execute(item));
    }

    private void OnRemoveApiCredentialClick(object sender, RoutedEventArgs e)
    {
        QueueProviderMenuAction(sender, item => ViewModel.ApiProviders.RemoveCredentialCommand.Execute(item));
    }

    private void OnRemoveApiProviderClick(object sender, RoutedEventArgs e)
    {
        QueueProviderMenuAction(sender, item => ViewModel.ApiProviders.RemoveApiProviderCommand.Execute(item));
    }

    private void OnMoveUpApiProviderClick(object sender, RoutedEventArgs e)
    {
        QueueProviderMenuAction(sender, item => ViewModel.ApiProviders.MoveUpCommand.Execute(item));
    }

    private void OnMoveDownApiProviderClick(object sender, RoutedEventArgs e)
    {
        QueueProviderMenuAction(sender, item => ViewModel.ApiProviders.MoveDownCommand.Execute(item));
    }

    private void OnApiProvidersReordered(object sender, DragItemsCompletedEventArgs e)
    {
        if (e.DropResult != DataPackageOperation.Move) return;
        var orderedIds = ViewModel.ApiProviders.Items.Select(a => a.Id).ToList();
        ViewModel.ApiProviders.ReorderCommand.Execute(orderedIds);
    }

    private void OnSelectChatGptTabClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTab = 0;
    }

    private void OnSelectApiProvidersTabClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTab = 1;
    }

    private void OnSelectLaunchProfilesTabClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTab = 2;
    }
}
