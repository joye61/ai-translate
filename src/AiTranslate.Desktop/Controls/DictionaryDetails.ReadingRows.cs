using AiTranslate.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AiTranslate.Desktop.Controls;

internal sealed partial class DictionaryDetails
{
    private sealed class CollocationGrid() : ReadingGrid("Auto,2*,*,*,Auto")
    {
        // Remember the logical cell as its visible position and span change in a narrow viewport.
        public sealed record Field(int Line, int Column, bool HasHint, bool HasAction);
        protected override int ContentColumn(Control cell) => cell.Tag is Field f ? f.Column : base.ContentColumn(cell);

        protected override void Reflow(double available, double[] widths)
        {
            // Use the data to choose the layout, independent of the visibility of the hint header.
            var hintWidth = Children.Where(c => c.Tag is Field { Line: > 0, Column: 3 }).Select(c => c.DesiredSize.Width).DefaultIfEmpty().Max();
            var compact = hintWidth > 0 && available < widths[0] + widths[4] + Math.Min(widths[1], 160) +
                Math.Min(widths[2], 100) + Math.Min(hintWidth, 100);
            foreach (var cell in Children)
            {
                if (cell.Tag is not Field f) continue;
                var isHint = f.Column == 3;
                if (f.Line == 0)
                {
                    if (isHint) cell.IsVisible = !compact;
                    else ((TextBlock)cell).Text = compact ? "译义与义项" : "译义";
                    continue;
                }
                var column = compact ? 2 : f.Column;
                var last = !compact && !isHint && f.HasHint ? 2 : f.HasAction ? 3 : 4;
                SetColumnSpan(cell, last - column + 1);
                SetColumn(cell, column);
                SetRow(cell, f.Line + (isHint && compact ? 1 : 0));
                cell.Margin = new Thickness(0, isHint && compact ? 0 : AppTheme.Space.S,
                    last == 4 ? AppTheme.Space.L : AppTheme.Space.M, compact && !isHint && f.HasHint ? AppTheme.Space.XXS : AppTheme.Space.S);
            }
            if (!compact) return;
            widths[2] = Math.Max(widths[2], widths[3]);
            widths[3] = 0;
        }
    }

    private void ShowCollocations(EntryBlockSpec spec, Row[] rows, bool passage)
    {
        var section = Ensure(spec);
        section.Host.IsVisible = rows.Length > 0;
        if (section.Items is not ReadingGrid table)
        {
            if (rows.Length == 0) return;
            table = new CollocationGrid();
            section.Items = table;
            section.Body.Content = RichBlocks.TableFrame(table);
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        var interactive = _finished is not null;
        var hasNotes = rows.Any(row => !string.IsNullOrWhiteSpace(row.Note));
        var updateHeader = table.Children.Count == 0 || section.HasLabels != hasNotes;
        bool Changed(int i) => section.Interactive != interactive || i >= rows.Length ||
            i >= section.Rows.Length || rows[i] != section.Rows[i];

        if (!updateHeader && section.Interactive == interactive && section.Rows.SequenceEqual(rows)) return;
        // Leave unchanged links, selections and disclosure controls in place as more rows stream in.
        for (var i = table.Children.Count - 1; i >= 0; i--)
        {
            var line = Grid.GetRow(table.Children[i]);
            if (line == 0 ? updateHeader : Changed((line - 1) / 3)) table.Children.RemoveAt(i);
        }
        var lines = 1 + 3 * rows.Length;
        while (table.RowDefinitions.Count > lines) table.RowDefinitions.RemoveAt(table.RowDefinitions.Count - 1);
        while (table.RowDefinitions.Count < lines) table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        if (updateHeader)
        {
            var band = new Border { Background = AppTheme.InputBg };
            Grid.SetColumnSpan(band, 5);
            table.AddCell(band, 0, 0);
            Header(1, passage ? "原文表达" : "搭配");
            Header(2, "译义");
            if (hasNotes) Header(3, "对应义项");
            void Header(int column, string label)
            {
                var caption = TextStyles.Create(label, TextRole.Label);
                if (column >= 2) caption.Tag = new CollocationGrid.Field(0, column, hasNotes, false);
                caption.Margin = new Thickness(0, AppTheme.Space.XS, AppTheme.Space.M, AppTheme.Space.XS);
                table.AddCell(caption, 0, column);
            }
        }
        for (var i = 0; i < rows.Length; i++)
            if (Changed(i)) AddCollocation(table, rows[i], 1 + i * 3);
        var counter = section.Section!.Caption;
        counter.Text = !passage && rows.Length > 1 ? $"{rows.Length} 条" : null;
        counter.IsVisible = counter.Text is not null;
        section.Rows = rows;
        section.Interactive = interactive;
        section.HasLabels = hasNotes;
    }

    private void AddCollocation(ReadingGrid table, Row row, int line)
    {
        var rule = new Border { BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetColumnSpan(rule, 5);
        table.AddCell(rule, line, 0);
        Add(StudioUi.CreateOrdinal(row.Number), 0);
        Add(_finished is null ? TextStyles.Selectable(row.Label, TextRole.Term) : TermButton(row), 1);
        var meaning = TextStyles.Selectable(row.Text, TextRole.ExampleTarget);
        if (!string.IsNullOrWhiteSpace(row.UsageNote))
        {
            var usage = StudioUi.CreateKeyValue("用法", RichText.Create(row.UsageNote, TextRole.Annotation));
            Add(new StackPanel { Spacing = AppTheme.Space.XXS, Children = { meaning, usage } }, 2);
        }
        else if (!string.IsNullOrWhiteSpace(row.Text)) Add(meaning, 2);
        if (!string.IsNullOrWhiteSpace(row.Note)) Add(RichText.Create(row.Note, TextRole.Annotation), 3);
        if (row.Example is { } example)
        {
            var details = new StackPanel { Margin = new Thickness(0, 0, AppTheme.Space.L, AppTheme.Space.M) };
            AddExample(details, example);
            Add(_disclosures.Toggle(EntryBlock.Collocations, row.Label, row.Number, row.Label, "例句", details, row.Number == 1), 4);
            Grid.SetColumnSpan(details, 4);
            table.AddCell(details, line + 2, 1);
        }

        void Add(Control cell, int column)
        {
            if (column is 2 or 3) cell.Tag = new CollocationGrid.Field(line, column, !string.IsNullOrWhiteSpace(row.Note), row.Example is not null);
            if (column == 1) Grid.SetRowSpan(cell, 2);
            cell.Margin += new Thickness(column == 0 ? AppTheme.Space.L : 0, AppTheme.Space.S,
                column == 4 ? AppTheme.Space.L : AppTheme.Space.M, AppTheme.Space.S);
            cell.VerticalAlignment = VerticalAlignment.Top;
            table.AddCell(cell, line, column);
        }
    }

    private static Grid ReadingRow(int number, Control summary, Button? toggle, StackPanel details)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto")
        };
        // Shared across the block: a sense group with a single (unnumbered) sense keeps the same text edge.
        grid.ColumnDefinitions[0].SharedSizeGroup = "Ordinal";
        grid.ColumnDefinitions[2].SharedSizeGroup = "DisclosureAction";
        if (number > 0)
        {
            var ordinal = StudioUi.CreateOrdinal(number);
            ordinal.Margin = new Thickness(0, 1, AppTheme.Space.M, 0);
            grid.Children.Add(ordinal);
        }
        summary.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(summary, 1);
        grid.Children.Add(summary);
        if (toggle is not null)
        {
            toggle.Margin = new Thickness(AppTheme.Space.S, 0, 0, 0);
            Grid.SetColumn(toggle, 2);
            grid.Children.Add(toggle);
        }
        if (details.Children.Count > 0)
        {
            details.Margin = new Thickness(0, AppTheme.Space.XS, 0, 0);
            Grid.SetRow(details, 1);
            Grid.SetColumn(details, 1);
            Grid.SetColumnSpan(details, 2);
            grid.Children.Add(details);
        }
        return grid;
    }

    private void AddExample(StackPanel body, BilingualExample example)
    {
        SelectableTextBlock line = _finished is null ? TextStyles.Selectable(example.SourceSentence, TextRole.ExampleSource)
            : TextStyles.Apply(new DictionaryReadingText(example.SourceSentence, (q, context) => lookup(q, context, false),
                [example.HighlightTerm], termForeground: AppTheme.AccentBright), TextRole.ExampleSource);
        var pair = new StackPanel { Spacing = 1, Children = { line } };
        if (!string.IsNullOrWhiteSpace(example.TranslatedSentence))
        {
            var translation = TextStyles.Selectable(example.TranslatedSentence, TextRole.ExampleTarget);
            if (_finished is not null) DictionaryReadingText.EnableSelectionLookup(translation, (q, context) => lookup(q, context, true));
            pair.Children.Add(translation);
        }
        var quote = new MdiIcon("format-quote-open", 16, AppTheme.TextMuted)
        {
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0)
        };
        Grid.SetColumn(pair, 1);
        var grid = new ExampleGrid(quote, pair)
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = AppTheme.Space.S, Children = { quote, pair }
        };
        if (_finished is not null && _readAccent is { } accent)
        {
            var read = SpeechPlaybackButton.Create(click => StudioUi.CreateCompactButton("volume-high", "跟读", click),
                playback => PhonicsAudioBar.SpeakSentence(speech, settings, example.SourceSentence, accent, playback));
            ToolTip.SetTip(read, "朗读这句例句");
            read.HorizontalAlignment = HorizontalAlignment.Right;
            read.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(read, 2);
            grid.Children.Add(read);
            grid.Actions = read;
        }
        body.Children.Add(new Border
        {
            Background = AppTheme.InputBg,
            CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S), Child = grid
        });
    }

    private sealed class ExampleGrid(Control quote, Control pair) : Grid
    {
        public Control? Actions { get; set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Actions is { } actions)
            {
                pair.Measure(Size.Infinity);
                actions.Measure(Size.Infinity);
                quote.Measure(Size.Infinity);
                var inlineWidth = quote.DesiredSize.Width + pair.DesiredSize.Width + actions.DesiredSize.Width + 2 * ColumnSpacing;
                var below = availableSize.Width < 520 && inlineWidth > availableSize.Width;
                SetColumnSpan(pair, below ? 2 : 1);
                SetColumn(actions, below ? 1 : 2);
                SetRow(actions, below ? 1 : 0);
                SetColumnSpan(actions, below ? 2 : 1);
                actions.Margin = below ? new Thickness(0, AppTheme.Space.XXS, 0, 0) : default;
            }
            return base.MeasureOverride(availableSize);
        }
    }
}
