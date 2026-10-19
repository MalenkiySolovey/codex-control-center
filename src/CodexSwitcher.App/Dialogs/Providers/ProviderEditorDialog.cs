using CodexSwitcher.App.Support;
using CodexSwitcher.Core.Common.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Dialogs.Providers;

internal enum ProviderEditorAction
{
    Cancel,
    Primary,
    Secondary,
}

/// <summary>
/// Shared wide, fixed-footer editor for adding and editing API providers.
/// It stays on the owner's XamlRoot so showing the modal does not create a
/// second WinUI Window from inside a provider MenuFlyout.
/// </summary>
internal sealed class ProviderEditorDialog
{
    private readonly ContentDialog _dialog;
    private Func<ProviderEditorAction, bool>? _validateAndCommit;
    private ProviderEditorAction _result = ProviderEditorAction.Cancel;

    public ScrollViewer BodyScrollViewer { get; }

    public ProviderEditorDialog(
        Window owner,
        string title,
        UIElement body,
        string primaryButtonText,
        string cancelButtonText,
        string? secondaryButtonText = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(body);

        var ownerRoot = owner.Content as FrameworkElement
            ?? throw new InvalidOperationException("The provider editor requires a XAML owner window.");
        var xamlRoot = ownerRoot.XamlRoot
            ?? throw new InvalidOperationException("The provider editor requires an attached XamlRoot.");
        var layout = SafeModalLayoutCalculator.Calculate(xamlRoot.Size.Width, xamlRoot.Size.Height);
        var width = Math.Min(layout.Width, 940);
        var maxHeight = Math.Min(layout.MaxHeight, 740);
        var contentMaxHeight = Math.Max(180, Math.Min(layout.ContentMaxHeight, maxHeight - 148));
        var contentWidth = Math.Max(0, width - 48);
        var footerButtonCount = string.IsNullOrWhiteSpace(secondaryButtonText) ? 2 : 3;
        var footerButtonWidth = Math.Max(0, (contentWidth - 48 - (12 * (footerButtonCount - 1))) / footerButtonCount);

        BodyScrollViewer = new ScrollViewer
        {
            Content = body,
            Padding = new Thickness(24, 12, 24, 16),
            Width = contentWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = contentMaxHeight,
        };
        BodyScrollViewer.SizeChanged += (_, _) => ConstrainBodyWidth();

        var footerButtons = new Grid
        {
            Width = Math.Max(0, contentWidth - 48),
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };

        var primaryButton = CreateFooterButton(primaryButtonText, primary: true, footerButtonWidth);
        primaryButton.Click += (_, _) => TryComplete(ProviderEditorAction.Primary);
        footerButtons.Children.Add(primaryButton);

        var cancelColumn = 1;
        if (!string.IsNullOrWhiteSpace(secondaryButtonText))
        {
            footerButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var secondaryButton = CreateFooterButton(secondaryButtonText, primary: false, footerButtonWidth);
            Grid.SetColumn(secondaryButton, 1);
            secondaryButton.Click += (_, _) => TryComplete(ProviderEditorAction.Secondary);
            footerButtons.Children.Add(secondaryButton);
            cancelColumn = 2;
        }

        var cancelButton = CreateFooterButton(cancelButtonText, primary: false, footerButtonWidth);
        Grid.SetColumn(cancelButton, cancelColumn);
        cancelButton.Click += (_, _) => Cancel();
        footerButtons.Children.Add(cancelButton);

        var footer = new Border
        {
            Width = contentWidth,
            Padding = new Thickness(24, 14, 24, 18),
            Background = Brushes.Resource("LayerFillColorAltBrush"),
            Child = footerButtons,
        };

        var content = new Grid
        {
            Width = contentWidth,
            MaxHeight = maxHeight - 48,
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
            },
        };
        content.Children.Add(BodyScrollViewer);
        Grid.SetRow(footer, 1);
        content.Children.Add(footer);

        _dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            DefaultButton = ContentDialogButton.None,
            XamlRoot = xamlRoot,
            RequestedTheme = ownerRoot.RequestedTheme,
            Width = width,
            MinWidth = width,
            MaxWidth = width,
            MaxHeight = maxHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        void CenterDialogInOwnerViewport()
        {
            if (!double.IsFinite(_dialog.ActualWidth) || _dialog.ActualWidth <= 0 ||
                !double.IsFinite(_dialog.ActualHeight) || _dialog.ActualHeight <= 0)
            {
                return;
            }

            var left = Math.Max(0, (xamlRoot.Size.Width - _dialog.ActualWidth) / 2);
            var top = Math.Max(0, (xamlRoot.Size.Height - _dialog.ActualHeight) / 2);
            var margin = new Thickness(left, top, 0, 0);
            if (Math.Abs(_dialog.Margin.Left - margin.Left) > 0.5 ||
                Math.Abs(_dialog.Margin.Top - margin.Top) > 0.5)
            {
                _dialog.Margin = margin;
            }
        }

        _dialog.Loaded += (_, _) => CenterDialogInOwnerViewport();
        _dialog.SizeChanged += (_, _) => CenterDialogInOwnerViewport();
    }

    public async Task<ProviderEditorAction> ShowAsync(Func<ProviderEditorAction, bool> validateAndCommit)
    {
        _validateAndCommit = validateAndCommit ?? throw new ArgumentNullException(nameof(validateAndCommit));
        _result = ProviderEditorAction.Cancel;
        await _dialog.ShowAsync();
        return _result;
    }

    private static Button CreateFooterButton(string text, bool primary, double width)
    {
        var button = new Button
        {
            Content = text,
            Width = width,
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

        _result = action;
        _dialog.Hide();
    }

    private void Cancel()
    {
        _result = ProviderEditorAction.Cancel;
        _dialog.Hide();
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
