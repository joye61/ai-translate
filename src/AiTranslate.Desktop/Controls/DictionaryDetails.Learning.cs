using AiTranslate.Core;
using Avalonia;
using Avalonia.Controls;

namespace AiTranslate.Desktop.Controls;

internal sealed partial class DictionaryDetails
{
    private Control CreateUsageGuidance(UsageGuidance guidance, int number)
    {
        var body = new StackPanel { Spacing = AppTheme.Space.S };
        body.Children.Add(TextStyles.Selectable(guidance.Title, TextRole.Term));
        body.Children.Add(RichText.Create(guidance.Explanation, TextRole.Secondary));
        if (guidance.Example is { } example) AddExample(body, example);
        return NumberedLearningRow(number, body);
    }

    private Control? CreatePronunciationPractice(DictionaryEntryResult entry)
    {
        var body = new StackPanel { Spacing = AppTheme.Space.M };
        var readAccent = _readAccent ?? (PhonicsAudioBar.HasEnglishAudio(entry) ? AiTranslate.Speech.SpeechAccent.AmericanEnglish : null);
        var drills = new List<Control>();
        foreach (var drill in entry.PronunciationDrills)
        {
            var row = new StackPanel { Spacing = AppTheme.Space.XS };
            if (!string.IsNullOrWhiteSpace(drill.Focus)) row.Children.Add(TextStyles.Selectable(drill.Focus, TextRole.Label));
            row.Children.Add(TextStyles.Selectable(drill.Text, TextRole.ExampleSource));
            if (!string.IsNullOrWhiteSpace(drill.Tip)) row.Children.Add(StudioUi.CreateKeyValue("要领", RichText.Create(drill.Tip, TextRole.Annotation)));
            if (_finished is not null && readAccent is { } accent)
            {
                var actions = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
                actions.Children.Add(PhonicsAudioBar.CreateDrillReadButton(speech, settings, drill, accent, slow: false));
                actions.Children.Add(PhonicsAudioBar.CreateDrillReadButton(speech, settings, drill, accent, slow: true));
                row.Children.Add(actions);
            }
            drills.Add(NumberedLearningRow(drills.Count + 1, row));
        }
        if (drills.Count > 0) body.Children.Add(RichBlocks.RuledRows(drills, framed: true));
        if (!string.IsNullOrWhiteSpace(entry.PronunciationTip))
            body.Children.Add(StudioUi.CreateKeyValue("发音提示", RichText.Create(entry.PronunciationTip, TextRole.Secondary)));
        if (_finished is not null && PhonicsAudioBar.HasEnglishAudio(entry))
        {
            var practice = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
            PhonicsAudioBar.AddPracticeButtons(practice, speech, settings, entry.SourceText, entry.UsPhonetic,
                entry.UkPhonetic, entry.Examples.FirstOrDefault()?.SourceSentence);
            body.Children.Add(practice);
        }
        return body.Children.Count == 0 ? null : body;
    }

    private static Control NumberedLearningRow(int number, Control body)
    {
        var ordinal = StudioUi.CreateOrdinal(number);
        ordinal.Margin = new Thickness(0, 0, AppTheme.Space.M, 0);
        Grid.SetColumn(body, 1);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { ordinal, body } };
    }
}
