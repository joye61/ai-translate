using AiTranslate.Core;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AiTranslate.Desktop.Controls;

internal sealed partial class DictionaryDetails
{
    private Control? CreateRelatedTable(IReadOnlyList<RelatedForm> forms)
    {
        if (forms.Count == 0) return null;
        return RichBlocks.Table("*,2*", ["关联词", "释义与用法"], forms.Select(RelatedCells).ToArray());
    }

    private IReadOnlyList<Control> RelatedCells(RelatedForm form)
    {
        var identity = new StackPanel { Spacing = AppTheme.Space.XXS };
        var notes = new StackPanel { Spacing = AppTheme.Space.XXS };
        var example = form.Expression?.Trim() ?? "";
        identity.Children.Add(_finished is null ? TextStyles.Selectable(form.Value, TextRole.Term)
            : TermButton(new Row(form.Value, form.Label), example));

        var part = LexicalPart.From(form.PartOfSpeech).Name;
        var relation = form.Relation switch
        {
            "derived" => "派生词",
            "counterpart" => "对应表达",
            "sameForm" => "同形用法",
            "confusable" => "易混词",
            _ => ""
        };
        var labels = new WrapPanel { ItemSpacing = AppTheme.Space.XS, LineSpacing = AppTheme.Space.XXS };
        if (part.Length > 0) labels.Children.Add(StudioUi.CreateTag(part, TextRole.Label, HueMap.PartOfSpeech(part)));
        if (relation.Length > 0)
        {
            var kind = TextStyles.Create(relation, TextRole.FieldLabel);
            kind.VerticalAlignment = VerticalAlignment.Center;
            labels.Children.Add(kind);
        }
        if (labels.Children.Count > 0) identity.Children.Add(labels);
        if (!string.IsNullOrWhiteSpace(form.Label) && LexicalPart.From(form.Label).Name != part)
        {
            var labelPart = LexicalPart.From(form.Label).Name;
            Control label = part.Length == 0 && labelPart.Length > 0 && !ReferenceEquals(HueMap.PartOfSpeech(labelPart), AppTheme.Hues.Neutral)
                ? StudioUi.CreateTag(labelPart, TextRole.Label, HueMap.PartOfSpeech(labelPart))
                : TextStyles.Selectable(form.Label, TextRole.Definition);
            notes.Children.Add(label);
        }

        if (!string.IsNullOrWhiteSpace(form.Usage)) notes.Children.Add(RichText.Create(form.Usage, TextRole.Secondary));
        if (example.Length > 0)
        {
            var phrase = _finished is null ? TextStyles.Selectable(example, TextRole.ExampleSource)
                : TextStyles.Apply(new DictionaryReadingText(example, (word, sentence) => lookup(word, sentence, false),
                    [form.Value], termForeground: AppTheme.AccentBright), TextRole.ExampleSource);
            notes.Children.Add(StudioUi.CreateKeyValue("示例", phrase));
        }
        return [identity, notes];
    }
}
