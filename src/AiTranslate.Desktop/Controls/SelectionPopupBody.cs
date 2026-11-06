using System.Diagnostics;
using AiTranslate.Core;
using AiTranslate.Speech;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

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
    private readonly TextBlock _hintLabel = TextStyles.Create(null, TextRole.Label);
    private readonly TextBlock _hintCue = TextStyles.Create(null, TextRole.Definition);
    private readonly Border _hintCard;
    private readonly LoadingSkeleton _skeleton = new();
    private readonly DispatcherTimer _frames = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Dictionary<string, (Control Row, TextBlock Definition)> _senseRows = [];
    private DictionaryEntryResult? _pending;
    private DictionaryEntryResult? _last;
    private string _primaryText = "", _literalText = "", _hintText = "";
    private long _lastFrame;
    private bool _attached, _finished;

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
                Spacing = AppTheme.Space.XXS,
                Children =
                {
                    _hintLabel,
                    _hintCue,
                    _hint
                }
            }
        };
        Children.AddRange([_translations, _senses, _audio, _hintCard, _skeleton]);
        _frames.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            AdvanceFrame(Stopwatch.GetElapsedTime(_lastFrame, now));
            _lastFrame = now;
        };
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            if (_pending is not null) StartFrames();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            _frames.Stop();
        };
    }

    public void ShowEntry(DictionaryEntryResult entry, bool complete)
    {
        if (_finished) return;
        _pending = entry;
        if (complete || entry.Origin is DictionaryResultOrigin.Cache or DictionaryResultOrigin.BuiltIn)
        {
            Render(entry, complete, TimeSpan.Zero, animate: false);
            _pending = null;
            _frames.Stop();
            if (complete)
            {
                _finished = true;
                Children.Remove(_skeleton);
            }
        }
        else if (_attached && !_frames.IsEnabled)
        {
            AdvanceFrame(_frames.Interval);
            if (_pending is not null) StartFrames();
        }
    }

    private void StartFrames()
    {
        _lastFrame = Stopwatch.GetTimestamp();
        _frames.Start();
    }

    internal void AdvanceFrame(TimeSpan elapsed)
    {
        if (_pending is not { } entry || _finished || !_attached) return;
        if (Render(entry, complete: false, elapsed, animate: true)) return;
        _pending = null;
        _frames.Stop();
    }

    private bool Render(DictionaryEntryResult entry, bool complete, TimeSpan elapsed, bool animate)
    {
        var unfinished = false;
        string Next(string shown, string target)
        {
            var next = animate ? StreamingText.Advance(shown, target, elapsed) : target;
            unfinished |= next != target;
            return next;
        }

        _primaryText = Next(_primaryText, entry.PrimaryTranslation);
        _literalText = Next(_literalText, entry.LiteralTranslation);
        _translations.ShowEntry(entry with { PrimaryTranslation = _primaryText, LiteralTranslation = _literalText }, complete ? _lookup : null);

        var groups = entry.Senses.GroupBy(s => LexicalPart.From(s.PartOfSpeech).Name).ToArray();
        foreach (var key in _senseRows.Keys.Except(groups.Select(g => g.Key)).ToArray())
        {
            _senses.Children.Remove(_senseRows[key].Row);
            _senseRows.Remove(key);
        }
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            if (!_senseRows.TryGetValue(group.Key, out var row))
            {
                var definition = TextStyles.Create(null, TextRole.Definition);
                var part = LexicalPart.From(group.First().PartOfSpeech);
                var label = part.Abbreviation.Length > 0 ? part.Abbreviation : part.Name;
                row = (label.Length == 0 ? definition : StudioUi.CreateKeyValue(label, definition, "PopupPos"), definition);
                _senseRows.Add(group.Key, row);
                _senses.Children.Insert(i, row.Row);
            }
            else if (_senses.Children.IndexOf(row.Row) != i) _senses.Children.Move(_senses.Children.IndexOf(row.Row), i);
            var meanings = string.Join("；", group.Select(s => s.DefinitionZh).Distinct());
            row.Definition.Text = Next(row.Definition.Text ?? "", meanings);
        }
        _senses.IsVisible = _senses.Children.Count > 0;

        // The popup is a compact preview; the workbench and notebook expose every strategy.
        var memory = entry.Mnemonics.FirstOrDefault();
        var etymology = entry.EtymologyNotes.FirstOrDefault();
        var mnemonic = memory is not null || (etymology is null && !string.IsNullOrWhiteSpace(entry.MemoryMnemonic));
        _hintLabel.Text = mnemonic ? "记忆助记" : "词源";
        _hintCue.Text = Next(_hintCue.Text ?? "", memory?.Cue ?? etymology?.Title ?? "");
        _hintCue.IsVisible = !string.IsNullOrWhiteSpace(_hintCue.Text);
        var hint = Next(_hintText, memory?.Explanation ?? etymology?.Explanation ??
            (mnemonic ? entry.MemoryMnemonic : entry.EtymologyAndRoot) ?? "");
        if (_hintText != hint)
        {
            _hintText = hint;
            RichText.Fill(_hint, hint);
        }
        _hint.IsVisible = !string.IsNullOrWhiteSpace(hint);
        _hintCard.IsVisible = _hintCue.IsVisible || _hint.IsVisible;
        _hintCard.Background = mnemonic ? AppTheme.AmberSoft : AppTheme.InputBg;

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
        // This is a placeholder for missing content, not a progress indicator for the
        // optional reviews that can continue long after the compact preview is readable.
        if (_translations.IsVisible || _senses.IsVisible || _hintCard.IsVisible)
            Children.Remove(_skeleton);
        _last = entry;
        return unfinished;
    }

    public void ShowError(string message)
    {
        _finished = true;
        _pending = null;
        _frames.Stop();
        Children.Remove(_skeleton);
        _audio.IsVisible = _senses.IsVisible = _hintCard.IsVisible = false;
        _translations.IsVisible = false;
        Children.Add(TextStyles.Selectable(message, TextRole.Body));
    }
}
