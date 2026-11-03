using AiTranslate.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AiTranslate.Desktop.Controls;

internal sealed partial class DictionaryDetails
{
    private Control CreateCollocation(Row row)
    {
        Control term = _finished is null ? TextStyles.Selectable(row.Label, TextRole.Term) : TermButton(row);
        term.VerticalAlignment = VerticalAlignment.Top;
        var meaning = new StackPanel { Spacing = AppTheme.Space.XXS };
        if (!string.IsNullOrWhiteSpace(row.Text)) meaning.Children.Add(TextStyles.Selectable(row.Text, TextRole.Body));
        if (!string.IsNullOrWhiteSpace(row.Note)) meaning.Children.Add(RichText.Create(row.Note, TextRole.Secondary));

        // Equal proportions across rows leave a stable reading edge for both languages.
        var summary = new Grid { ColumnDefinitions = new ColumnDefinitions("*,2*"), ColumnSpacing = AppTheme.Space.M };
        summary.Children.Add(term);
        Grid.SetColumn(meaning, 1);
        summary.Children.Add(meaning);
        summary.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        summary.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        summary.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 320;
            Grid.SetColumnSpan(term, narrow ? 2 : 1);
            Grid.SetRow(meaning, narrow ? 1 : 0);
            Grid.SetColumn(meaning, narrow ? 0 : 1);
            Grid.SetColumnSpan(meaning, narrow ? 2 : 1);
        };
        var details = new StackPanel();
        Button? toggle = null;
        if (row.Example is { } example)
        {
            AddExample(details, example, row.Label);
            toggle = _disclosures.Toggle(EntryBlock.Collocations, row.Label, row.Number, row.Label, "例句", details, row.Number == 1);
        }
        return ReadingRow(row.Number, summary, toggle, details);
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

    private void AddExample(StackPanel body, BilingualExample example, string? expression)
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
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = AppTheme.Space.S, Children = { quote, pair }
        };
        if (_finished is { } entry)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
            if (_readAccent is { } accent)
            {
                var read = SpeechPlaybackButton.Create(click => StudioUi.CreateCompactButton("volume-high", "跟读", click),
                    playback => PhonicsAudioBar.SpeakSentence(speech, settings, example.SourceSentence, accent, playback));
                ToolTip.SetTip(read, "朗读这句例句");
                actions.Children.Add(read);
            }
            if (SentenceCloze.FromExample(example, entry.SourceText, entry.Inflections, expression) is { } cloze)
            {
                var quiz = StudioUi.CreateCompactButton("school", "自测", () => practice(cloze));
                AutomationProperties.SetName(quiz, "用这句自测");
                ToolTip.SetTip(quiz, "遮住表达，用这句例句自测回忆");
                actions.Children.Add(quiz);
            }
            if (actions.Children.Count > 0)
            {
                Grid.SetColumn(actions, 2);
                actions.HorizontalAlignment = HorizontalAlignment.Right;
                grid.Children.Add(actions);
                grid.SizeChanged += (_, e) =>
                {
                    var narrow = e.NewSize.Width < 520;
                    Grid.SetColumnSpan(pair, narrow ? 2 : 1);
                    Grid.SetColumn(actions, narrow ? 1 : 2);
                    Grid.SetRow(actions, narrow ? 1 : 0);
                    Grid.SetColumnSpan(actions, narrow ? 2 : 1);
                    actions.Margin = narrow ? new Thickness(0, AppTheme.Space.XXS, 0, 0) : default;
                };
            }
        }
        body.Children.Add(new Border
        {
            Background = AppTheme.InputBg,
            CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S), Child = grid
        });
    }
}
