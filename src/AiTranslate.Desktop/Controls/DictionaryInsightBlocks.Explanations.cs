using AiTranslate.Core;
using Avalonia.Controls;

namespace AiTranslate.Desktop.Controls;

internal static partial class DictionaryInsightBlocks
{
    public static Control? Explanations(IReadOnlyList<EntryExplanation> notes, bool cultural = false)
    {
        if (notes.Count == 0) return null;
        var entries = notes.Select(note =>
        {
            var row = new StackPanel { Spacing = AppTheme.Space.XXS };
            if (!string.IsNullOrWhiteSpace(note.Title)) row.Children.Add(TextStyles.Selectable(note.Title, TextRole.Term));
            if (!string.IsNullOrWhiteSpace(note.Explanation))
                row.Children.Add(RichText.Create(note.Explanation, string.IsNullOrWhiteSpace(note.Title) ? TextRole.Body : TextRole.Secondary));
            return row;
        });
        if (cultural) return RichBlocks.RuledRows(entries, framed: true);
        var notesColumn = new StackPanel { Spacing = AppTheme.Space.M };
        foreach (var entry in entries) notesColumn.Children.Add(entry);
        return notesColumn;
    }

    public static Control? Mnemonics(IReadOnlyList<MnemonicStrategy> strategies)
    {
        if (strategies.Count == 0) return null;
        var list = new StackPanel { Spacing = AppTheme.Space.M };
        foreach (var strategy in strategies)
        {
            var row = new StackPanel { Spacing = AppTheme.Space.XXS };
            if (!string.IsNullOrWhiteSpace(strategy.Method)) row.Children.Add(TextStyles.Selectable(strategy.Method, TextRole.Label));
            if (!string.IsNullOrWhiteSpace(strategy.Cue)) row.Children.Add(TextStyles.Selectable(strategy.Cue, TextRole.Definition));
            if (!string.IsNullOrWhiteSpace(strategy.Explanation)) row.Children.Add(RichText.Create(strategy.Explanation, TextRole.Secondary));
            list.Children.Add(row);
        }
        return RichBlocks.Callout("lightbulb-on-outline", list, RichBlocks.CalloutTone.Attention);
    }

    public static Control? ToneEvidence(IReadOnlyList<ToneEvidence> evidence) => evidence.Count == 0 ? null :
        RichBlocks.RuledRows(evidence.Select(e =>
        {
            var item = new StackPanel { Spacing = AppTheme.Space.XS };
            if (!string.IsNullOrWhiteSpace(e.Aspect)) item.Children.Add(TextStyles.Selectable(e.Aspect, TextRole.Term));
            if (!string.IsNullOrWhiteSpace(e.Quote))
                item.Children.Add(RichBlocks.Quote(TextStyles.Selectable(e.Quote, TextRole.ExampleSource)));
            if (!string.IsNullOrWhiteSpace(e.Explanation)) item.Children.Add(RichText.Create(e.Explanation, TextRole.Secondary));
            return item;
        }), framed: true);

    public static Control? Combine(params Control?[] blocks)
    {
        var present = blocks.OfType<Control>().ToArray();
        if (present.Length == 0) return null;
        if (present.Length == 1) return present[0];
        var body = new StackPanel { Spacing = AppTheme.Space.M };
        foreach (var block in present) body.Children.Add(block);
        return body;
    }
}
