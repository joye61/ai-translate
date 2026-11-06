using AiTranslate.Desktop.Controls;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Views;

/// <summary>Small modal windows shared by the notebook page: a name prompt and a destructive confirmation.</summary>
internal static class VaultDialogs
{
    internal static Window Create(Window owner, string title, Control body, double width = 420, double? height = null, bool flushBottom = false)
    {
        var window = new Window
        {
            Icon = owner.Icon,
            Title = title, Width = Math.Min(width, owner.ClientSize.Width), MaxHeight = owner.ClientSize.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanMinimize = false, CanMaximize = false,
            Background = AppTheme.CardBg, FontFamily = AppTheme.BodyFont,
            SizeToContent = height.HasValue ? SizeToContent.Manual : SizeToContent.Height
        };
        if (height.HasValue) window.Height = Math.Min(height.Value, owner.ClientSize.Height);
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            window.Close();
        };
        if (!flushBottom) body.Margin += new Thickness(0, 0, 0, AppTheme.Space.XL);
        WindowTitleBar.SetDialogContent(window, body);
        return window;
    }

    /// <summary>Asks for a notebook name. <paramref name="save"/> throws to keep the dialog open with the message.</summary>
    internal static async Task PromptName(Window owner, string title, string? initial, Action<string> save)
    {
        const int MaxLength = 30;
        var inset = new Thickness(AppTheme.Space.XL, 0);
        var name = new TextBox
        {
            Text = initial, PlaceholderText = "例如：论文阅读", MaxLength = MaxLength, FontSize = AppTheme.Type.Body, MinHeight = 38,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(AppTheme.Radius.M), Background = AppTheme.InputBg
        };
        AutomationProperties.SetName(name, "生词本名称");
        var counter = TextStyles.Create("", TextRole.Meta, wrap: false);
        counter.HorizontalAlignment = HorizontalAlignment.Right;
        void Count() => counter.Text = $"{name.Text?.Length ?? 0} / {MaxLength}";
        Count();
        var caption = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { TextStyles.Create("名称", TextRole.FieldLabel, wrap: false), counter }
        };
        Grid.SetColumn(counter, 1);

        var errorText = TextStyles.Create("", TextRole.FieldLabel);
        errorText.Foreground = AppTheme.Danger;
        var error = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS, IsVisible = false,
            Children = { new FontAwesomeIcon("circle-info", 14, AppTheme.Danger) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) }, errorText }
        };
        var field = new StackPanel { Spacing = AppTheme.Space.XS, Children = { caption, name, error } };
        string? rejected = null;
        name.TextChanged += (_, _) =>
        {
            Count();
            if (name.Text != rejected) error.IsVisible = false;
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.S, HorizontalAlignment = HorizontalAlignment.Right
        };
        var dialog = Create(owner, title, new StackPanel
        {
            Margin = inset, Spacing = AppTheme.Space.XL, Children = { field, buttons }
        });
        void Submit()
        {
            try
            {
                var value = name.Text?.Trim();
                if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("请输入生词本名称。");
                save(value);
                dialog.Close();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                errorText.Text = ex.Message;
                error.IsVisible = true;
                rejected = name.Text;
                name.Focus();
            }
        }
        var submit = StudioUi.CreatePrimaryButton("check", "保存", Submit);
        AutomationProperties.SetName(submit, "保存");
        buttons.Children.Add(StudioUi.CreateSecondaryButton("xmark", "取消", dialog.Close));
        buttons.Children.Add(submit);
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Submit();
        };
        dialog.Opened += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
        };
        await dialog.ShowDialog(owner);
    }

    internal static async Task<bool> ConfirmDelete(Window owner, string title, string message)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.S, HorizontalAlignment = HorizontalAlignment.Right
        };
        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Background = AppTheme.DangerSoft,
            VerticalAlignment = VerticalAlignment.Top, Child = new FontAwesomeIcon("regular/trash-can", 18, AppTheme.Danger)
        };
        var text = TextStyles.Create(message, TextRole.Secondary);
        text.VerticalAlignment = VerticalAlignment.Center;
        var summary = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.L, Children = { icon, text }
        };
        Grid.SetColumn(text, 1);
        var dialog = Create(owner, title, new StackPanel
        {
            Margin = new Thickness(AppTheme.Space.XL, 0), Spacing = AppTheme.Space.XL,
            Children = { summary, buttons }
        });
        var cancel = StudioUi.CreateSecondaryButton("xmark", "取消", () => dialog.Close(false));
        buttons.Children.Add(cancel);
        buttons.Children.Add(StudioUi.CreateDangerButton("regular/trash-can", "确认删除", () => dialog.Close(true)));
        dialog.Opened += (_, _) => cancel.Focus();
        return await dialog.ShowDialog<bool>(owner);
    }
}

