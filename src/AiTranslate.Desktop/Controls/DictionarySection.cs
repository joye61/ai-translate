using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AiTranslate.Desktop.Controls;

/// <summary>A section in the continuous dictionary reading column.</summary>
internal sealed class DictionarySection : Border
{
    public TextBlock Caption { get; } = TextStyles.Apply(new TextBlock
    {
        IsVisible = false, VerticalAlignment = VerticalAlignment.Center
    }, TextRole.Meta);

    private readonly TextBlock _title;

    public string Title
    {
        get => _title.Text ?? "";
        set => _title.Text = value;
    }

    public DictionarySection(string title, Control body, bool insight = false)
    {
        BorderBrush = AppTheme.BorderSubtle;
        BorderThickness = new Thickness(0, 1, 0, 0);
        Margin = new Thickness(0, AppTheme.Space.XL, 0, 0);
        Padding = new Thickness(0, AppTheme.Space.L, 0, 0);
        var heading = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XXS };
        _title = TextStyles.Create(title, TextRole.SectionTitle, wrap: false);
        _title.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(_title);
        if (insight)
        {
            var insightIcon = new MdiIcon("sparkles", 14, AppTheme.Insight) { VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(insightIcon, "AI 按内容判断生成的深度解析");
            heading.Children.Add(insightIcon);
        }
        heading.Children.Add(Caption);
        Child = new StackPanel { Spacing = AppTheme.Space.M, Children = { heading, body } };
    }
}
