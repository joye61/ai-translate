using AiTranslate.Core;
using Avalonia;
using Avalonia.Controls;

namespace AiTranslate.Desktop.Controls;

/// <summary>The main reading and its optional literal counterpart, shared by all lookup surfaces.</summary>
internal sealed class DictionaryTranslations : StackPanel
{
    private readonly TextBlock _naturalLabel = TextStyles.Create("自然译文", TextRole.FieldLabel);
    private readonly SelectableTextBlock _preview = TextStyles.Selectable(null, TextRole.Translation);
    private readonly Border _primary = new();
    private readonly TextBlock _literalLabel = TextStyles.Create(EntryBlockCatalog.LiteralTranslation.Title, TextRole.FieldLabel);
    private readonly SelectableTextBlock _literal = TextStyles.Selectable(null, TextRole.Secondary);
    private readonly Grid _literalHeader = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    private readonly TextBlock _reviewError = BlockReviewActions.CreateError();

    public bool HasLinkedTerms => _primary.Child is DictionaryReadingText { HasLinkedTerms: true };

    public DictionaryTranslations()
    {
        Spacing = AppTheme.Space.XS;
        _literalHeader.Margin = new Thickness(0, AppTheme.Space.S, 0, 0);
        _literalLabel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _literalHeader.Children.Add(_literalLabel);
        _primary.Child = _preview;
        Children.AddRange([_naturalLabel, _primary, _literalHeader, _reviewError, _literal]);
        _naturalLabel.IsVisible = _literalHeader.IsVisible = _literal.IsVisible = IsVisible = false;
    }

    public void ShowPreview(string text, VocabularyItemType type)
    {
        TextStyles.Apply(_preview, type.IsPassage() ? TextRole.PassageTranslation : TextRole.Translation);
        _preview.Text = text;
        _primary.Child = _preview;
        IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    public void ShowEntry(DictionaryEntryResult entry, Action<string, string>? lookup, BlockReviewActions? reviews = null)
    {
        if (lookup is null) ShowPreview(entry.PrimaryTranslation, entry.ItemType);
        else
        {
            _primary.Child = DictionaryReadingText.Primary(entry, lookup);
            IsVisible = true;
        }
        _literal.Text = entry.LiteralTranslation;
        _naturalLabel.IsVisible = _literalHeader.IsVisible = _literal.IsVisible = !string.IsNullOrWhiteSpace(_literal.Text);
        if (_literal.IsVisible && reviews is not null && _literalHeader.Children.Count == 1)
        {
            var button = reviews.Create(EntryBlockCatalog.LiteralTranslation, _reviewError, draft => _literal.Text = draft.LiteralTranslation);
            Grid.SetColumn(button, 1);
            _literalHeader.Children.Add(button);
        }
        if (_literal.IsVisible && lookup is not null) DictionaryReadingText.EnableSelectionLookup(_literal, lookup);
    }
}
