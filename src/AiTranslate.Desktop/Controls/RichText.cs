using AiTranslate.Core;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace AiTranslate.Desktop.Controls;

/// <summary>
/// Renders <see cref="InlineMarkup"/> in AI explanations. Emphasis changes color only, never font weight:
/// a weight change mid-paragraph makes later CJK runs render bold. Strong text takes the insight color
/// (grammar anchors additionally carry a tinted background, so the two stay distinguishable).
/// </summary>
internal static class RichText
{
    public static SelectableTextBlock Create(string? text, TextRole role)
    {
        var block = TextStyles.Selectable(null, role);
        Fill(block, text);
        return block;
    }

    public static void Fill(TextBlock block, string? text)
    {
        var spans = InlineMarkup.Parse(text);
        // Plain text keeps Text populated so selection, copy and existing lookups behave exactly as before.
        if (spans.All(s => s.Style == InlineStyle.Plain))
        {
            block.Inlines?.Clear();
            block.Text = text;
            return;
        }
        block.Text = null;
        block.Inlines ??= [];
        block.Inlines.Clear();
        foreach (var span in spans)
            block.Inlines.Add(span.Style switch
            {
                InlineStyle.Strong => new Run(span.Text) { Foreground = AppTheme.Insight },
                InlineStyle.Quote => new Run(span.Text) { Foreground = AppTheme.TextPrimary, Background = AppTheme.CodeBg },
                _ => new Run(span.Text)
            });
    }
}
