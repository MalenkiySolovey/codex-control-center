using CodexSwitcher.App.Support;
using CodexSwitcher.Core.Common.Layout;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace CodexSwitcher.App.Dialogs.Providers;

internal enum ProviderEditorAction
{
    Cancel,
    Primary,
    Secondary,
}

/// <summary>
/// Shared wide, fixed-footer shell for Add API Provider and Edit API Provider.
/// The editor is a modal WinUI window rather than a ContentDialog so the form
/// can use the full desktop width while keeping its footer visible.
/// </summary>
internal sealed partial class ProviderEditorWindow : Window
{
    private readonly Window _owner;
    private readonly Control _ownerRoot;
    private readonly TaskCompletionSource<ProviderEditorAction> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<ProviderEditorAction, bool>? _validateAndCommit;
    private bool _presenterConfigured;

    public ScrollViewer BodyScrollViewer { get; }

    public ProviderEditorWindow(
        Window owner,
        string title,
        UIElement body,
        string primaryButtonText,
        string cancelButtonText,
        string? secondaryButtonText = null)
    {
        InitializeComponent();
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _ownerRoot = owner.Content as Control
            ?? throw new InvalidOperationException("The provider editor requires a XAML owner window.");

        Title = title;

        BodyScrollViewer = new ScrollViewer
        {
            Content = body,
            Padding = new Thickness(24, 12, 24, 16),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        BodyScrollViewer.SizeChanged += (_, _) => ConstrainBodyWidth();

        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
            },
            Background = Brushes.Resource("LayerFillColorDefaultBrush"),
        };

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var headingBorder = new Border
        {
            Padding = new Thickness(24, 18, 24, 10),
            Child = heading,
        };
        root.Children.Add(headingBorder);

        Grid.SetRow(BodyScrollViewer, 1);
        root.Children.Add(BodyScrollViewer);

        var footerButtons = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };

        var primaryButton = CreateFooterButton(primaryButtonText, primary: true);
        primaryButton.Click += (_, _) => TryComplete(ProviderEditorAction.Primary);
        footerButtons.Children.Add(primaryButton);

        if (!string.IsNullOrWhiteSpace(secondaryButtonText))
        {
            footerButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var secondaryButton = CreateFooterButton(secondaryButtonText, primary: false);
            Grid.SetColumn(secondaryButton, 1);
            secondaryButton.Click += (_, _) => TryComplete(ProviderEditorAction.Secondary);
            footerButtons.Children.Add(secondaryButton);

            var cancelButton = CreateFooterButton(cancelButtonText, primary: false);
            Grid.SetColumn(cancelButton, 2);
            cancelButton.Click += (_, _) => Cancel();
            footerButtons.Children.Add(cancelButton);
        }
        else
        {
            var cancelButton = CreateFooterButton(cancelButtonText, primary: false);
            Grid.SetColumn(cancelButton, 1);
            cancelButton.Click += (_, _) => Cancel();
            footerButtons.Children.Add(cancelButton);
        }

        var footer = new Border
        {
            Padding = new Thickness(24, 14, 24, 18),
            Background = Brushes.Resource("LayerFillColorAltBrush"),
            Child = footerButtons,
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        root.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape)
            {
                Cancel();
                args.Handled = true;
            }
        };

        root.RequestedTheme = _ownerRoot.RequestedTheme;

        var editorSurface = new Border
        {
            Background = Brushes.Resource("LayerFillColorDefaultBrush"),
            BorderBrush = Brushes.Resource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = root,
        };
        WindowRoot.RequestedTheme = _ownerRoot.RequestedTheme;
        WindowRoot.Children.Add(editorSurface);

        WindowRoot.Loaded += ConfigurePresenterAfterContentLoaded;
        Closed += (_, _) => _completion.TrySetResult(ProviderEditorAction.Cancel);
    }

    public async Task<ProviderEditorAction> ShowAsync(Func<ProviderEditorAction, bool> validateAndCommit)
    {
        _validateAndCommit = validateAndCommit ?? throw new ArgumentNullException(nameof(validateAndCommit));
        ConfigureWindow();
        Activate();
        return await _completion.Task;
    }

    private static Button CreateFooterButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40,
            Height = 40,
        };
        if (primary)
        {
            button.Background = Brushes.Resource("BrandAccentBrush");
            button.Foreground = Brushes.Resource("BrandOnAccentBrush");
        }
        return button;
    }

    private void TryComplete(ProviderEditorAction action)
    {
        if (_validateAndCommit?.Invoke(action) != true)
        {
            return;
        }

        _completion.TrySetResult(action);
        Close();
    }

    private void Cancel()
    {
        _completion.TrySetResult(ProviderEditorAction.Cancel);
        Close();
    }

    private void ConfigureWindow()
    {
        var ownerXamlRoot = _ownerRoot.XamlRoot;
        var scale = ownerXamlRoot.RasterizationScale;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = 1;
        }

        var layout = SafeModalLayoutCalculator.Calculate(ownerXamlRoot.Size.Width, ownerXamlRoot.Size.Height);
        var widthDip = Math.Min(layout.Width, 940);
        var heightDip = Math.Min(layout.MaxHeight, 740);
        BodyScrollViewer.MaxHeight = Math.Max(180, Math.Min(layout.ContentMaxHeight, heightDip - 148));

        var ownerAppWindow = _owner.AppWindow;
        var appWindow = AppWindow;
        appWindow.IsShownInSwitchers = false;

        var widthPx = Math.Max(1, (int)Math.Round(widthDip * scale));
        var heightPx = Math.Max(1, (int)Math.Round(heightDip * scale));
        appWindow.Resize(new SizeInt32(widthPx, heightPx));

        var workArea = DisplayArea.GetFromWindowId(ownerAppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var marginPx = (int)Math.Round(24 * scale);
        var x = ownerAppWindow.Position.X + (ownerAppWindow.Size.Width - widthPx) / 2;
        var y = ownerAppWindow.Position.Y + (ownerAppWindow.Size.Height - heightPx) / 2;
        var minX = workArea.X + marginPx;
        var minY = workArea.Y + marginPx;
        var maxX = workArea.X + workArea.Width - widthPx - marginPx;
        var maxY = workArea.Y + workArea.Height - heightPx - marginPx;
        if (maxX >= minX) x = Math.Clamp(x, minX, maxX);
        if (maxY >= minY) y = Math.Clamp(y, minY, maxY);
        appWindow.Move(new PointInt32(x, y));
    }

    private void ConfigurePresenterAfterContentLoaded(object sender, RoutedEventArgs args)
    {
        if (_presenterConfigured || AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        _presenterConfigured = true;
        try
        {
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            _ownerRoot.IsEnabled = false;
            Closed += (_, _) => _ownerRoot.IsEnabled = true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Provider editor window chrome configuration failed: {exception}");
            _ownerRoot.IsEnabled = true;
        }
    }

    private void ConstrainBodyWidth()
    {
        if (BodyScrollViewer.Content is not FrameworkElement body)
        {
            return;
        }

        var availableWidth = BodyScrollViewer.ActualWidth -
            BodyScrollViewer.Padding.Left -
            BodyScrollViewer.Padding.Right -
            18;
        if (double.IsFinite(availableWidth) && availableWidth > 0)
        {
            body.Width = availableWidth;
        }
    }
}
