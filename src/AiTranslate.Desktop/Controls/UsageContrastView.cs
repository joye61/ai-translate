using AiTranslate.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

internal static class UsageContrastView
{
    //  ┌ by serendipity ─────┐      ┌ by chance ──────────┐
    //  │ 偶然地（强调惊喜）     │  vs  │ 偶然地（中性）        │
    //  └─────────────────────┘      └─────────────────────┘
    // Separate text blocks per weight: a CJK Run after a SemiBold Run renders bold in Avalonia.
    public static Control Create(UsageContrast contrast)
    {
        var left = Side(contrast.LeftForm, contrast.LeftUse, AppTheme.Hues.Sky);
        var right = Side(contrast.RightForm, contrast.RightUse, AppTheme.Hues.Honey);
        if (left is null || right is null) return (Control?)left ?? (Control?)right ?? new Panel();
        var versus = new TextBlock
        {
            Text = "vs", FontSize = AppTheme.Type.Small, FontWeight = FontWeight.SemiBold, FontStyle = FontStyle.Italic,
            Foreground = AppTheme.TextMuted, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(versus, 1);
        Grid.SetColumn(right, 2);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), ColumnSpacing = AppTheme.Space.M,
            Children = { left, versus, right }
        };
    }

    private static Border? Side(string form, string use, Hue hue)
    {
        if (string.IsNullOrWhiteSpace(form)) return null;
        var term = TextStyles.Selectable(form, TextRole.Term);
        term.Foreground = hue.Ink;
        var body = new StackPanel { Spacing = 2, Children = { term } };
        if (!string.IsNullOrWhiteSpace(use)) body.Children.Add(TextStyles.Selectable(use, TextRole.Secondary));
        return new Border
        {
            Background = hue.Soft, CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(AppTheme.Space.M + 2, AppTheme.Space.S + 2), Child = body
        };
    }
}
