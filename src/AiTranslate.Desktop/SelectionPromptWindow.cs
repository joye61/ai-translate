using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AiTranslate.Desktop;

/// <summary>Small chip shown at a selection that has no local translation yet. Clicking it opens the regular lookup.</summary>
internal sealed class SelectionPromptWindow : Window
{
    private const int MaxQueryLength = 24;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);
    private readonly TextBlock _label;
    private readonly Button _button;
    private readonly DispatcherTimer _expiry = new() { Interval = Lifetime };
    private Action? _open;

    public SelectionPromptWindow(IDesktopPlatformService? platform = null)
    {
        Title = "AI 划词提示";
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 300;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Transparent;

        _label = TextStyles.Apply(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        }, TextRole.FieldLabel);
        _label.Foreground = AppTheme.AccentBright;
        _button = new Button
        {
            Classes = { "tint" },
            Background = AppTheme.CardBg,
            BorderBrush = AppTheme.BorderStrong,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(AppTheme.Radius.Pill),
            Padding = new Thickness(AppTheme.Space.L, AppTheme.Space.S),
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.S,
                Children = { new FontAwesomeIcon("language", 16, AppTheme.AccentBright), _label }
            }
        };
        _button.Click += (_, _) =>
        {
            var open = _open;
            Hide();
            open?.Invoke();
        };
        Content = _button;

        _expiry.Tick += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        Deactivated += (_, _) => Hide();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible)
            {
                _expiry.Stop();
                _expiry.Start();
                if (platform is not null) platform.PointerPressed += DismissOutside;
            }
            else
            {
                _expiry.Stop();
                _open = null;
                if (platform is not null) platform.PointerPressed -= DismissOutside;
            }
        };
    }

    public void ShowAt(PixelPoint anchor, string query, Action open)
    {
        _open = open;
        var shown = query.Length > MaxQueryLength ? query[..MaxQueryLength] + "…" : query;
        _label.Text = $"翻译 “{shown}”";
        ToolTip.SetTip(_button, "在翻译界面中查询选中的文字");

        var screen = Screens.ScreenFromPoint(anchor) ?? Screens.Primary;
        var position = anchor + new PixelPoint(12, 16);
        if (screen is not null)
        {
            _button.Measure(new Size(MaxWidth, double.PositiveInfinity));
            position = SelectionPopupPlacement.Constrain(position, _button.DesiredSize, screen.Scaling, screen.WorkingArea);
        }
        Position = position;
        if (IsVisible)
        {
            _expiry.Stop();
            _expiry.Start();
        }
        else Show();
    }

    private void DismissOutside(PointD point) => Dispatcher.UIThread.Post(() =>
    {
        if (IsVisible && !new Rect(ClientSize).Contains(this.PointToClient(new PixelPoint((int)point.X, (int)point.Y))))
            Hide();
    });
}
