using AiTranslate.Core;
using AiTranslate.Speech;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AiTranslate.Desktop.Controls;

internal sealed class SelectionPopupBody : StackPanel
{
    private readonly ISpeechSynthesisService _speech;
    private readonly AppSettingsStore _settings;
    private readonly Border _audio = new() { IsVisible = false };
    private readonly DictionaryTranslations _translations = new();
    private readonly Action<string, string> _lookup;
    private readonly StackPanel _senses = new() { Spacing = AppTheme.Space.XS, IsVisible = false };
    private readonly TextBlock _hint = TextStyles.Create(null, TextRole.Secondary);
    private readonly Border _hintCard;
    private DictionaryEntryResult? _last;

    public SelectionPopupBody(ISpeechSynthesisService speech, AppSettingsStore settings, Action<string, string> lookup)
    {
        _speech = speech;
        _settings = settings;
        _lookup = lookup;
        Spacing = 10;
        Grid.SetIsSharedSizeScope(_senses, true);
        _hintCard = new Border
        {
            Background = AppTheme.InputBg,
            CornerRadius = new CornerRadius(AppTheme.Radius.S),
            Padding = new Thickness(AppTheme.Space.M - 2, AppTheme.Space.S),
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS,
                        Children =
                        {
                            new MdiIcon("lightbulb-on-outline", 14, AppTheme.Amber),
                            TextStyles.Create("巧记与词源", TextRole.Label, wrap: false)
                        }
                    },
                    _hint
                }
            }
        };
        Children.AddRange([_translations, _senses, _audio, _hintCard]);
    }

    public void ShowEntry(DictionaryEntryResult entry, bool complete)
    {
        _translations.ShowEntry(entry, complete ? _lookup : null);

        if (_last is null || !entry.Senses.SequenceEqual(_last.Senses))
        {
            _senses.Children.Clear();
            foreach (var group in entry.Senses.GroupBy(s => LexicalPart.From(s.PartOfSpeech).Name))
            {
                var meanings = string.Join("；", group.Select(s => s.DefinitionZh).Distinct());
                var definition = TextStyles.Create(meanings, TextRole.Definition);
                var part = LexicalPart.From(group.First().PartOfSpeech);
                var label = part.Abbreviation.Length > 0 ? part.Abbreviation : part.Name;
                _senses.Children.Add(label.Length == 0 ? definition : StudioUi.CreateKeyValue(label, definition, "PopupPos"));
            }
            _senses.IsVisible = _senses.Children.Count > 0;
        }

        if (_last is null || _last.MemoryMnemonic != entry.MemoryMnemonic || _last.EtymologyAndRoot != entry.EtymologyAndRoot)
        {
            var hint = !string.IsNullOrWhiteSpace(entry.MemoryMnemonic) ? entry.MemoryMnemonic : entry.EtymologyAndRoot;
            RichText.Fill(_hint, hint);
            _hintCard.IsVisible = !string.IsNullOrWhiteSpace(hint);
        }

        var accent = PhonicsAudioBar.SourceAccent(entry.DetectedSourceLanguage);
        if (accent is { } voice && (complete || entry.Senses.Count > 0) &&
            (!_audio.IsVisible || _last?.DetectedSourceLanguage != entry.DetectedSourceLanguage ||
             _last.ItemType != entry.ItemType || _last.UsPhonetic != entry.UsPhonetic || _last.UkPhonetic != entry.UkPhonetic))
        {
            _audio.Child = voice == SpeechAccent.AmericanEnglish && !entry.ItemType.IsPassage()
                ? PhonicsAudioBar.CreateWordPhonicsStrip(_speech, _settings, entry.SourceText, entry.UsPhonetic, entry.UkPhonetic, compact: true)
                : PhonicsAudioBar.CreateSentenceReadButton(_speech, _settings, entry.SourceText, "朗读原文", voice);
            _audio.IsVisible = true;
        }
        if (accent is null) _audio.IsVisible = false;
        _last = entry;
    }

    public void ShowError(string message)
    {
        _audio.IsVisible = _senses.IsVisible = _hintCard.IsVisible = false;
        _translations.IsVisible = false;
        Children.Add(TextStyles.Selectable(message, TextRole.Body));
    }
}
