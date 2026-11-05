using AiTranslate.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

/// <summary>One selectable text flow for copying, with optional grammar marks and linked expressions.</summary>
internal sealed class DictionaryReadingText : SelectableTextBlock
{
    private sealed record Mark(int Start, int End, string? Term, string? GrammarNote = null);
    private const string SelectionHint = "选中文字后右键查词";
    private static readonly Cursor TextCursor = Cursor.Parse("IBeam");
    private static readonly Cursor LinkCursor = Cursor.Parse("Hand");
    private readonly List<Mark> _marks = [];
    private readonly Action<string, string> _lookup;
    private readonly string _original;
    private readonly bool _standaloneTerms;
    private Mark? _pressed;

    public bool HasLinkedTerms => _marks.Any(mark => mark.Term is not null);
    // TextDecoration belongs to the creating UI dispatcher.
    // A thin, offset hairline reads as "clickable" without the heavy black rule of a default underline.
    internal static TextDecorationCollection LinkUnderline(IBrush? stroke = null) =>
    [
        new TextDecoration
        {
            Location = TextDecorationLocation.Underline, Stroke = stroke ?? AppTheme.LinkUnderline,
            StrokeThickness = 1.2, StrokeThicknessUnit = TextDecorationUnit.Pixel,
            StrokeOffset = 3, StrokeOffsetUnit = TextDecorationUnit.Pixel
        }
    ];
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    internal static DictionaryReadingText Primary(DictionaryEntryResult entry, Action<string, string> lookup)
    {
        var text = entry.PrimaryTranslation;
        if (entry.ItemType.IsPassage())
            return Passage(text, lookup, entry.Collocations.Concat(entry.GlossaryReferences).Select(c => c.Meaning));
        var terms = entry.TranslationOptions.Select(o => o.Text).Concat(entry.PrimaryRenderings());
        return TextStyles.Apply(new DictionaryReadingText(text, lookup, terms, standaloneTerms: true), TextRole.Translation);
    }

    internal static DictionaryReadingText Passage(string text, Action<string, string> lookup, IEnumerable<string> terms,
        IEnumerable<GrammarHighlight>? grammar = null) =>
        TextStyles.Apply(new DictionaryReadingText(text, lookup,
            terms.Where(term => !string.Equals(term?.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase)), grammar),
            TextRole.PassageTranslation);

    public DictionaryReadingText(string text, Action<string, string> lookup, IEnumerable<string> terms,
        IEnumerable<GrammarHighlight>? grammar = null, IBrush? termForeground = null, bool standaloneTerms = false)
    {
        _original = text;
        _standaloneTerms = standaloneTerms;
        _lookup = lookup;
        Background = Brushes.Transparent;
        TextWrapping = TextWrapping.Wrap;
        LineHeight = 23;
        foreach (var highlight in grammar ?? [])
        {
            if (string.IsNullOrEmpty(highlight.Quote)) continue;
            var at = DictionaryTextMatch.IndexOf(text, highlight.Quote, comparison: StringComparison.Ordinal);
            if (at >= 0) _marks.Add(new(at, at + highlight.Quote.Length, null, $"{highlight.Label} · {highlight.Explanation}"));
        }
        foreach (var term in terms.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).OrderByDescending(t => t.Length))
        {
            var start = 0;
            while (start < text.Length)
            {
                var at = DictionaryTextMatch.IndexOf(text, term, start,
                    standaloneTerms ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                start = at + term.Length;
                if (!_marks.Any(m => m.Term is not null && m.Start < start && at < m.End))
                    _marks.Add(new(at, start, text.Substring(at, term.Length)));
            }
        }
        if (_marks.Count == 0) Text = text;
        else if (_marks.Count == 1 && _marks[0].Term == text && termForeground is null)
        {
            Text = text;
            TextDecorations = LinkUnderline();
        }
        else
        {
            // Split at both kinds of boundaries: a clickable word can lie inside a longer grammar span.
            var stops = _marks.SelectMany(m => new[] { m.Start, m.End }).Append(0).Append(text.Length).Distinct().Order().ToArray();
            for (var i = 1; i < stops.Length; i++)
            {
                var start = stops[i - 1];
                var run = new Run(text[start..stops[i]]);
                if (_marks.Any(m => m.Term is null && m.Start <= start && start < m.End))
                {
                    run.Background = AppTheme.InsightSoft;
                    run.Foreground = AppTheme.Insight;
                    run.FontWeight = FontWeight.SemiBold;
                }
                if (_marks.Any(m => m.Term is not null && m.Start <= start && start < m.End))
                {
                    run.TextDecorations = LinkUnderline(termForeground is null ? null : AppTheme.AccentUnderline);
                    // Color only: a weight change mid-paragraph makes later CJK runs render bold.
                    if (termForeground is not null) run.Foreground = termForeground;
                }
                Inlines!.Add(run);
            }
        }
        EnableSelectionLookup(this, lookup);
    }

    public static void EnableSelectionLookup(SelectableTextBlock block, Action<string, string> lookup)
    {
        var find = new MenuItem { Header = "查询选中文本", [!IsEnabledProperty] = block[!CanCopyProperty] };
        find.Click += (_, _) =>
        {
            var selected = block.SelectedText;
            var query = DictionaryQuery.CleanInput(selected);
            if (query.Length == 0) return;
            var text = block.Text ?? block.Inlines?.Text ?? "";
            lookup(query, SentenceAt(text, Math.Min(block.SelectionStart, block.SelectionEnd), selected.Length));
        };
        var copy = new MenuItem { Header = "复制", [!IsEnabledProperty] = block[!CanCopyProperty] };
        copy.Click += (_, _) => block.Copy();
        var select = new MenuItem { Header = "全选" };
        select.Click += (_, _) => block.SelectAll();
        block.ContextMenu = new ContextMenu { Items = { find, copy, select } };
        ToolTip.SetTip(block, SelectionHint);
    }

    internal static string SentenceAt(string text, int index, int length)
    {
        if (index < 0 || index > text.Length || length > text.Length - index) return "";
        static bool EndsSentence(string value, int i) => value[i] is '。' or '！' or '？' or '!' or '?' or '\r' or '\n' ||
            value[i] == '.' && (i + 1 == value.Length || char.IsWhiteSpace(value[i + 1]));
        var left = index;
        var right = index + length;
        while (left > 0 && !EndsSentence(text, left - 1)) left--;
        while (right < text.Length && (right == index || !EndsSentence(text, right - 1))) right++;
        return text[left..right].Trim();
    }

    private int HitIndex(PointerEventArgs e)
    {
        var point = e.GetPosition(this) - new Point(Padding.Left, Padding.Top);
        var layout = TextLayout;
        var hit = layout.HitTestPoint(point);
        var index = hit.CharacterHit.FirstCharacterIndex;
        // On wrapped lines Avalonia can return the right character with IsInside == false.
        // Verify its layout bounds so blank space past the line still does nothing.
        if (index < 0 || index >= _original.Length) return -1;
        return hit.IsInside || layout.HitTestTextRange(index, 1).Any(bounds => bounds.Contains(point)) ? index : -1;
    }

    private Mark? TermAt(int index) =>
        _marks.FirstOrDefault(m => m.Term is not null && m.Start <= index && index < m.End);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var index = HitIndex(e);
        var term = TermAt(index);
        Cursor = term is null ? TextCursor : LinkCursor;
        var note = _marks.FirstOrDefault(m => m.GrammarNote is not null && m.Start <= index && index < m.End)?.GrammarNote;
        ToolTip.SetTip(this, term is null ? note ?? SelectionHint : $"查词 {term.Term}");
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _pressed = e.ClickCount == 1 && e.KeyModifiers == KeyModifiers.None && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            ? TermAt(HitIndex(e)) : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var pressed = _pressed;
        _pressed = null;
        base.OnPointerReleased(e);
        if (pressed?.Term is { } term && SelectionStart == SelectionEnd && TermAt(HitIndex(e)) == pressed)
            _lookup(term, _standaloneTerms || term == _original ? "" : SentenceAt(_original, pressed.Start, term.Length));
    }
}
