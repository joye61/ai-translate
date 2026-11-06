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
    public static Control Create(UsageContrast contrast, Action<StackPanel, BilingualExample>? addExample = null)
    {
        var left = Side(contrast.LeftForm, contrast.LeftUse, contrast.LeftExample, AppTheme.Hues.Sky, addExample);
        var right = Side(contrast.RightForm, contrast.RightUse, contrast.RightExample, AppTheme.Hues.Honey, addExample);
        if (left is null || right is null) return (Control?)left ?? (Control?)right ?? new Panel();
        var versus = new TextBlock
        {
            Text = "vs", FontSize = AppTheme.Type.Small, FontWeight = FontWeight.SemiBold, FontStyle = FontStyle.Italic,
            Foreground = AppTheme.TextMuted, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(versus, 1);
        Grid.SetColumn(right, 2);
        return new ContrastGrid(left, versus, right)
        {
            ColumnSpacing = AppTheme.Space.M,
            Children = { left, versus, right }
        };
    }

    // Keep corresponding fields side by side at reading width; give each example room on narrow panels.
    private sealed class ContrastGrid(Control left, Control versus, Control right) : Grid
    {
        private bool? _stacked;
        protected override Size MeasureOverride(Size availableSize)
        {
            var stacked = availableSize.Width < 520;
            if (_stacked != stacked)
            {
                _stacked = stacked;
                ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,Auto,*");
                RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto,Auto" : "Auto");
                RowSpacing = stacked ? AppTheme.Space.S : 0;
                SetColumn(left, 0);
                SetColumn(versus, stacked ? 0 : 1);
                SetColumn(right, stacked ? 0 : 2);
                SetRow(versus, stacked ? 1 : 0);
                SetRow(right, stacked ? 2 : 0);
                versus.HorizontalAlignment = HorizontalAlignment.Center;
            }
            return base.MeasureOverride(availableSize);
        }
    }

    private static Border? Side(string form, string use, BilingualExample? example, Hue hue,
        Action<StackPanel, BilingualExample>? addExample)
    {
        if (string.IsNullOrWhiteSpace(form)) return null;
        var term = TextStyles.Selectable(form, TextRole.Term);
        term.Foreground = hue.Ink;
        var body = new StackPanel { Spacing = AppTheme.Space.S, Children = { term } };
        if (!string.IsNullOrWhiteSpace(use)) body.Children.Add(RichText.Create(use, TextRole.Secondary));
        if (example is not null) addExample?.Invoke(body, example);
        return new Border
        {
            Background = hue.Soft, CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(AppTheme.Space.M + 2, AppTheme.Space.S + 2), Child = body
        };
    }
}
