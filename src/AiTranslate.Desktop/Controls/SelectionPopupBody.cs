using AiTranslate.Core;
using AiTranslate.Speech;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

internal sealed class SelectionPopupBody : Grid
{
    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(180);
    private readonly DraftPaperLoading _draft = new() { MinHeight = 64 };
    private readonly StackPanel _reading = new() { Spacing = 10, Opacity = 0 };
    private readonly ISpeechSynthesisService _speech;
    private readonly AppSettingsStore _settings;
    private readonly Border _audio = new() { IsVisible = false };
    private readonly TextBlock _translation = TextStyles.Create(null, TextRole.Translation);
    private readonly StackPanel _senses = new() { Spacing = AppTheme.Space.XS, IsVisible = false };
    private readonly TextBlock _hint = TextStyles.Create(null, TextRole.Secondary);
    private readonly Border _hintCard;
    private DictionaryEntryResult? _last;

    public SelectionPopupBody(ISpeechSynthesisService speech, AppSettingsStore settings)
    {
        _speech = speech;
        _settings = settings;
        MinHeight = 116;
        RowDefinitions = new RowDefinitions("Auto,*");
        _translation.FontSize = AppTheme.Type.BodyLarge;
        _translation.LineHeight = 23;
        _translation.IsVisible = false;
        SetIsSharedSizeScope(_senses, true);
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
        _reading.Children.AddRange([_audio, _translation, _senses, _hintCard]);
        _reading.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = RevealDuration }];
        Children.Add(_reading);
        SetRow(_draft, 1);
        Children.Add(_draft);
        _draft.SizeChanged += (_, e) =>
        {
            if (_draft.MinHeight == 0 && e.NewSize.Height == 0) _draft.IsVisible = false;
        };
    }

    public void ShowEntry(DictionaryEntryResult entry, bool complete)
    {
        _translation.Text = entry.PrimaryTranslation;
        _translation.IsVisible = !string.IsNullOrWhiteSpace(entry.PrimaryTranslation);
        if (_translation.IsVisible && !complete)
        {
            _draft.Margin = new Thickness(0, AppTheme.Space.S, 0, 0);
            _draft.ShowDetailCaption(entry.ItemType.IsPassage());
        }

        if (_last is null || !entry.Senses.Take(3).SequenceEqual(_last.Senses.Take(3)))
        {
            _senses.Children.Clear();
            foreach (var sense in entry.Senses.Take(3))
            {
                var definition = TextStyles.Create(sense.DefinitionZh, TextRole.Body);
                _senses.Children.Add(string.IsNullOrWhiteSpace(sense.PartOfSpeech) ? definition
                    : StudioUi.CreateKeyValue(sense.PartOfSpeech, definition, "PopupPos"));
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
        if (entry.Origin is DictionaryResultOrigin.Cache or DictionaryResultOrigin.BuiltIn) _reading.Transitions = null;
        if (_translation.IsVisible || _senses.IsVisible || _hintCard.IsVisible) _reading.Opacity = 1;
        if (complete) EndDraft(entry.Origin == DictionaryResultOrigin.Ai);
    }

    public void ShowError(string message)
    {
        _audio.IsVisible = _senses.IsVisible = _hintCard.IsVisible = false;
        _translation.Text = message;
        _translation.IsVisible = true;
        _reading.Opacity = 1;
        EndDraft(animate: false);
    }

    private void EndDraft(bool animate)
    {
        var height = _draft.Bounds.Height;
        MinHeight = 0;
        if (!animate || !IsEffectivelyVisible || height < 1)
        {
            _draft.IsVisible = false;
            return;
        }

        // Let new fields occupy the sheet's space during the fade, so the window never measures both heights.
        _draft.Margin = new Thickness(0, _draft.Bounds.Y, 0, 0);
        _draft.VerticalAlignment = VerticalAlignment.Top;
        SetRow(_draft, 0);
        _draft.MinHeight = 0;
        _draft.Height = height;
        _draft.Transitions =
        [
            new DoubleTransition { Property = HeightProperty, Duration = RevealDuration },
            new DoubleTransition { Property = OpacityProperty, Duration = RevealDuration }
        ];
        _draft.Opacity = 0;
        _draft.Height = 0;
    }
}
