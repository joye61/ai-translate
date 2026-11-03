using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

internal static class WindowTitleBar
{
    public static void SetContent(Window window, Control body)
    {
        window.WindowDecorations = WindowDecorations.BorderOnly;
        window.ExtendClientAreaToDecorationsHint = true;
        window.Content = new Border
        {
            [!Layoutable.MarginProperty] = window[!Window.WindowDecorationMarginProperty],
            Child = body
        };
    }

    public static StackPanel CreateButtons(Window window)
    {
        var mac = OperatingSystem.IsMacOS();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = AppTheme.Space.XXXL,
            Margin = mac ? new Thickness(AppTheme.Space.M, 0, AppTheme.Space.XXS, 0) : default
        };
        buttons.Classes.Set("traffic-lights", mac);

        var close = CreateButton(WindowDecorationsElementRole.CloseButton, new MdiIcon("close"), "关闭",
            window.Close, AppTheme.MacClose);
        if (mac) buttons.Children.Add(close);
        if (window.CanMinimize)
            buttons.Children.Add(CreateButton(WindowDecorationsElementRole.MinimizeButton, new MdiIcon("window-minimize"),
                "最小化", () => window.WindowState = WindowState.Minimized, AppTheme.MacMinimize));
        if (window.CanMaximize)
        {
            var icon = new MdiIcon();
            var expanded = mac ? WindowState.FullScreen : WindowState.Maximized;
            var toggle = CreateButton(mac ? WindowDecorationsElementRole.FullScreenButton : WindowDecorationsElementRole.MaximizeButton,
                icon, mac ? "进入全屏" : "最大化", () =>
                    window.WindowState = window.WindowState == expanded ? WindowState.Normal : expanded, AppTheme.MacZoom);
            void UpdateToggle()
            {
                var restore = window.WindowState == expanded;
                icon.IconName = mac ? (restore ? "arrow-collapse" : "arrow-expand") : (restore ? "window-restore" : "window-maximize");
                SetLabel(toggle, mac ? (restore ? "退出全屏" : "进入全屏") : (restore ? "还原" : "最大化"));
            }
            UpdateToggle();
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty) UpdateToggle();
            };
            buttons.Children.Add(toggle);
        }
        if (!mac) buttons.Children.Add(close);
        return buttons;
    }

    // Only passive surfaces belong here. Editors, menus and caption buttons are separate siblings.
    public static Border CreateDragArea(Window window, Control? decoration = null)
    {
        if (decoration is not null) decoration.IsHitTestVisible = false;
        var dragArea = new Border
        {
            Background = Brushes.Transparent,
            Child = decoration
        };
        if (OperatingSystem.IsLinux())
        {
            // X11's TitleBar hit test starts a move on every press, including the second click.
            dragArea.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(dragArea).Properties.IsLeftButtonPressed) return;
                if (e.ClickCount == 2 && window.CanMaximize)
                    window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else
                    window.BeginMoveDrag(e);
                e.Handled = true;
            };
        }
        else
            WindowDecorationProperties.SetElementRole(dragArea, WindowDecorationsElementRole.TitleBar);
        return dragArea;
    }

    public static Grid CreateWorkspaceToolbar(Window window, ContentControl tools, Control? trailingButtons)
    {
        var dragArea = CreateDragArea(window);
        Grid.SetColumnSpan(dragArea, 2);
        tools.HorizontalAlignment = HorizontalAlignment.Left;
        tools.VerticalAlignment = VerticalAlignment.Center;
        tools.Margin = new Thickness(AppTheme.Space.XXL, AppTheme.Space.M, AppTheme.Space.XXXL, AppTheme.Space.S);
        var row = new Grid
        {
            MinHeight = 64,
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { dragArea, tools }
        };
        if (trailingButtons is not null)
        {
            trailingButtons.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(trailingButtons, 1);
            row.Children.Add(trailingButtons);
        }
        return row;
    }

    public static void SetDialogContent(Window window, Control body)
    {
        var buttons = CreateButtons(window);
        var mac = OperatingSystem.IsMacOS();
        buttons.VerticalAlignment = mac ? VerticalAlignment.Center : VerticalAlignment.Top;
        DockPanel.SetDock(buttons, mac ? Dock.Left : Dock.Right);
        var heading = CreateDragArea(window, new TextBlock
        {
            [!TextBlock.TextProperty] = window[!Window.TitleProperty],
            Margin = new Thickness(mac ? AppTheme.Space.S : AppTheme.Space.XL, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = AppTheme.Type.Title, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.TextPrimary
        });
        Grid.SetRow(body, 1);
        SetContent(window, new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { new DockPanel { MinHeight = 52, Children = { buttons, heading } }, body }
        });
    }

    private static Button CreateButton(WindowDecorationsElementRole role, MdiIcon icon, string label, Action click, IBrush light)
    {
        var mac = OperatingSystem.IsMacOS();
        icon.IconSize = mac ? 10 : 16;
        var button = new Button
        {
            Classes = { "ghost", "caption" },
            Width = mac ? 21 : 46,
            Padding = default,
            BorderThickness = default,
            CornerRadius = default,
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = mac ? new Border
            {
                Width = 13, Height = 13, CornerRadius = new CornerRadius(AppTheme.Radius.Pill),
                Background = light, Child = icon
            } : icon
        };
        button.Classes.Set("caption-close", !mac && role == WindowDecorationsElementRole.CloseButton);
        WindowDecorationProperties.SetElementRole(button, role);
        SetLabel(button, label);
        button.Click += (_, _) => click();
        return button;
    }

    private static void SetLabel(Button button, string label)
    {
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
    }
}
