using AiTranslate.Core;
using Avalonia;
using Avalonia.Controls;

namespace AiTranslate.Desktop.Controls;

/// <summary>One numbered pair per sentence. Narrow readers get the translation directly under its source.</summary>
internal sealed class SentenceAlignmentGrid : Grid
{
    private readonly Border _band = new() { Background = AppTheme.InputBg, BorderBrush = AppTheme.BorderSubtle,
        BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly TextBlock _sourceHeader = TextStyles.Create("原文", TextRole.Label);
    private readonly TextBlock _targetHeader = TextStyles.Create("译文", TextRole.Label);
    private readonly List<(Control Source, Control Target, Control SourceLabel, Control TargetLabel)> _pairs = [];
    private bool? _stacked;

    public SentenceAlignmentGrid(IReadOnlyList<AlignedSentence> sentences, Func<string, SelectableTextBlock> source,
        Action<string, string>? targetLookup)
    {
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        SetColumnSpan(_band, 3);
        Add(_band, 0, 0);
        _sourceHeader.Margin = _targetHeader.Margin = new Thickness(AppTheme.Space.M, AppTheme.Space.XS);
        Add(_sourceHeader, 0, 1);
        Add(_targetHeader, 0, 2);
        for (var i = 0; i < sentences.Count; i++)
        {
            var sentence = sentences[i];
            var line = 1 + 2 * i;
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (i > 0)
            {
                var rule = new Border { BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(0, 1, 0, 0) };
                SetColumnSpan(rule, 3);
                Add(rule, line, 0);
            }
            var ordinal = StudioUi.CreateOrdinal(i + 1);
            ordinal.Margin = new Thickness(AppTheme.Space.M, AppTheme.Space.S, 0, 0);
            Add(ordinal, line, 0);
            var original = TextStyles.Apply(source(sentence.Source), TextRole.ExampleSource);
            var translation = TextStyles.Selectable(sentence.Target, TextRole.ExampleTarget);
            if (targetLookup is not null) DictionaryReadingText.EnableSelectionLookup(translation, targetLookup);
            var sourceLabel = TextStyles.Create("原文", TextRole.Label);
            var targetLabel = TextStyles.Create("译文", TextRole.Label);
            Add(original, line, 1);
            Add(translation, line, 2);
            Add(sourceLabel, line, 1);
            Add(targetLabel, line + 1, 1);
            _pairs.Add((original, translation, sourceLabel, targetLabel));
        }
    }

    private void Add(Control field, int row, int column)
    {
        SetRow(field, row);
        SetColumn(field, column);
        Children.Add(field);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var stacked = availableSize.Width < 520;
        if (_stacked != stacked)
        {
            _stacked = stacked;
            ColumnDefinitions = new ColumnDefinitions(stacked ? "Auto,Auto,*" : "Auto,*,*");
            _band.IsVisible = _sourceHeader.IsVisible = _targetHeader.IsVisible = !stacked;
            for (var i = 0; i < _pairs.Count; i++)
            {
                var (source, target, sourceLabel, targetLabel) = _pairs[i];
                sourceLabel.IsVisible = targetLabel.IsVisible = stacked;
                SetColumn(source, stacked ? 2 : 1);
                SetRow(target, 1 + 2 * i + (stacked ? 1 : 0));
                var side = AppTheme.Space.M;
                var gap = AppTheme.Space.S;
                source.Margin = new Thickness(side, gap, side, stacked ? 0 : gap);
                target.Margin = new Thickness(side, stacked ? AppTheme.Space.XS : gap, side, gap);
                sourceLabel.Margin = new Thickness(gap, gap + 1, 0, 0);
                targetLabel.Margin = new Thickness(gap, AppTheme.Space.XS + 1, 0, 0);
            }
        }
        return base.MeasureOverride(availableSize);
    }
}
