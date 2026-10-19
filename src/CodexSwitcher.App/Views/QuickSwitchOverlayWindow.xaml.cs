using System.Runtime.InteropServices;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.ViewModels;
using CodexSwitcher.App.Shell;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace CodexSwitcher.App.Views;

public sealed partial class QuickSwitchOverlayWindow : Window
{
    private const int OverlayWidth = 420;
    private const int OverlayHeight = 620;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private readonly IntPtr _anchorWindow;
    private readonly AppWindow _appWindow;

    public Strings Loc => Strings.Current;

    public event Action<AccountItemViewModel>? AccountSwitchRequested;
    public event Action<ApiProviderItemViewModel>? ApiProviderSwitchRequested;

    public QuickSwitchOverlayWindow(ShellViewModel viewModel, ElementTheme theme, IntPtr anchorWindow)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        Root.DataContext = viewModel;
        Root.RequestedTheme = theme;
        _anchorWindow = anchorWindow;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Title = Loc.QuickSwitchOverlayTitle;
        _appWindow.Resize(new SizeInt32(OverlayWidth, OverlayHeight));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        PositionNearAnchor(hwnd);
        Activated += OnActivated;
    }

    private void PositionNearAnchor(IntPtr overlayHandle)
    {
        var anchor = TryGetWindowRect(_anchorWindow, out var anchorRect)
            ? anchorRect
            : new NativeRect(0, 0, OverlayWidth, OverlayHeight);
        var monitor = MonitorFromWindow(_anchorWindow, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            monitorInfo.WorkArea = new NativeRect(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
        }

        var work = monitorInfo.WorkArea;
        var left = Math.Clamp(anchor.Right - OverlayWidth - 20, work.Left + 8, Math.Max(work.Left + 8, work.Right - OverlayWidth - 8));
        var top = Math.Clamp(anchor.Top + 72, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - OverlayHeight - 8));
        SetWindowPos(overlayHandle, HwndTopmost, left, top, OverlayWidth, OverlayHeight, SwpShowWindow);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Close();
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountItemViewModel account } && account.CanSwitch)
        {
            Close();
            AccountSwitchRequested?.Invoke(account);
        }
    }

    private void ApiProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ApiProviderItemViewModel provider } && provider.CanSwitch)
        {
            Close();
            ApiProviderSwitchRequested?.Invoke(provider);
        }
    }

    private static bool TryGetWindowRect(IntPtr hwnd, out NativeRect rect)
    {
        rect = default;
        return hwnd != IntPtr.Zero && IsWindow(hwnd) && GetWindowRect(hwnd, out rect);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
