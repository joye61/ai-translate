using AiTranslate.Core;
using AiTranslate.Speech;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

/// <summary>
/// Reusable KokoroSharp Phonics & Multi-Accent Speech Bar:
/// - US Pronunciation (`volume-high`, IPA badge)
/// - UK Pronunciation (`volume-high`, IPA badge)
/// - Natural Phonics Syllable Breakdown (`wave-square` 自然拼读音节拆解领读)
/// - Slow Shadow-Reading (`gauge` 0.68x 慢速跟读)
/// - Vocabulary Memory Loop (`repeat` 美音→英音→原句磨耳朵循环读)
/// </summary>
public static class PhonicsAudioBar
{
    internal static SpeechAccent? SourceAccent(string language) => language.Split('-')[0].ToLowerInvariant() switch
    {
        "en" => SpeechAccent.AmericanEnglish,
        "zh" => SpeechAccent.MandarinChinese,
        "ja" => SpeechAccent.Japanese,
        _ => null
    };

    public static Control CreateWordPhonicsStrip(
        ISpeechSynthesisService speech,
        AppSettingsStore settings,
        string headword,
        string? usIpa,
        string? ukIpa,
        string? exampleSentence = null,
        bool compact = false,
        SpeechPlayback? usPlayback = null)
    {
        var wrap = CreateAccentStrip(speech, settings, headword, usIpa, ukIpa, usPlayback);
        AddPracticeButtons(wrap, speech, settings, headword, usIpa, ukIpa, exampleSentence, compact);
        return wrap;
    }

    internal static bool HasEnglishAudio(DictionaryEntryResult entry) => !entry.ItemType.IsPassage() &&
        (SourceAccent(entry.DetectedSourceLanguage) == SpeechAccent.AmericanEnglish ||
        entry.DetectedSourceLanguage == "auto" && (!string.IsNullOrWhiteSpace(entry.UsPhonetic) || !string.IsNullOrWhiteSpace(entry.UkPhonetic)));

    internal static WrapPanel CreateAccentStrip(ISpeechSynthesisService speech, AppSettingsStore settings,
        string headword, string? usIpa, string? ukIpa, SpeechPlayback? usPlayback = null)
    {
        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };

        // 1. US Accent Button
        var usLabel = !string.IsNullOrWhiteSpace(usIpa) ? $"美 {usIpa}" : "美音 US";
        wrap.Children.Add(CreateSpeechPill(
            "volume-high",
            usLabel,
            playback => speech.SpeakAsync(
                headword,
                SpeechAccent.AmericanEnglish,
                usIpa,
                settings.Current.SpeechSpeed,
                settings.Current.PreferredUsVoiceId, playback: playback), usPlayback));

        // 2. UK Accent Button
        var ukLabel = !string.IsNullOrWhiteSpace(ukIpa) ? $"英 {ukIpa}" : "英音 UK";
        wrap.Children.Add(CreateSpeechPill(
            "volume-high",
            ukLabel,
            playback => speech.SpeakAsync(
                headword,
                SpeechAccent.BritishEnglish,
                ukIpa,
                settings.Current.SpeechSpeed,
                settings.Current.PreferredUkVoiceId, playback: playback)));

        if (headword.Length <= 48)
        {
            CancellationTokenSource? pending = null;
            wrap.AttachedToVisualTree += async (_, _) =>
            {
                using var lifetime = new CancellationTokenSource();
                pending = lifetime;
                try
                {
                    var prefs = settings.Current;
                    await speech.PreparePronunciationAsync(headword, SpeechAccent.AmericanEnglish, usIpa,
                        prefs.SpeechSpeed, prefs.PreferredUsVoiceId, lifetime.Token);
                    if (!lifetime.IsCancellationRequested)
                        await speech.PreparePronunciationAsync(headword, SpeechAccent.BritishEnglish, ukIpa,
                            prefs.SpeechSpeed, prefs.PreferredUkVoiceId, lifetime.Token);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                finally { if (pending == lifetime) pending = null; }
            };
            wrap.DetachedFromVisualTree += (_, _) => pending?.Cancel();
        }
        return wrap;
    }

    internal static void AddPracticeButtons(WrapPanel wrap, ISpeechSynthesisService speech, AppSettingsStore settings,
        string headword, string? usIpa, string? ukIpa, string? exampleSentence, bool compact = false)
    {
        if (!headword.Any(char.IsWhiteSpace) && !(headword.Length > 1 && headword.All(char.IsUpper)))
        {
            var syllables = KokoroSpeechService.SplitIntoPhonicsSyllables(headword);
            var syllableHint = syllables.Count > 1 && !compact && headword.Length <= 18
                ? $"自然拼读 [{string.Join("·", syllables)}]"
                : "自然拼读";
            wrap.Children.Add(CreateSpeechPill("wave-square", syllableHint,
                playback => speech.SpeakPhonicsBreakdownAsync(headword, usIpa, SpeechAccent.AmericanEnglish, playback: playback)));
        }

        if (!compact)
        {
            // 4. 0.68x Slow Shadow-Reading (慢速精听跟读)
            wrap.Children.Add(CreateSpeechPill(
                "gauge",
                "0.7x 慢读跟读",
                playback => speech.SpeakAsync(
                    headword,
                    SpeechAccent.AmericanEnglish,
                    usIpa,
                    speed: 0.68f,
                    settings.Current.PreferredUsVoiceId, playback: playback)));

            // 5. Vocabulary Memory Loop (美音 -> 英音 -> 例句连读)
            wrap.Children.Add(CreateSpeechPill(
                "repeat",
                "磨耳朵连读",
                playback => speech.SpeakVocabularyCardAsync(headword, usIpa, ukIpa, exampleSentence, playback: playback)));
        }
    }

    public static Button CreateSentenceReadButton(
        ISpeechSynthesisService speech,
        AppSettingsStore settings,
        string sentence,
        string label = "朗读例句",
        SpeechAccent accent = SpeechAccent.AmericanEnglish)
    {
        return CreateSpeechPill("volume-high", label, playback => SpeakSentence(speech, settings, sentence, accent, playback));
    }

    internal static Task SpeakSentence(ISpeechSynthesisService speech, AppSettingsStore settings, string sentence, SpeechAccent accent, SpeechPlayback playback) =>
        speech.SpeakAsync(
            sentence,
            accent,
            ipaPhonetic: null,
            speed: settings.Current.SpeechSpeed,
            preferredVoiceId: accent == SpeechAccent.AmericanEnglish ? settings.Current.PreferredUsVoiceId : null,
            playback: playback);

    internal static Button CreateDrillReadButton(ISpeechSynthesisService speech, AppSettingsStore settings,
        PronunciationDrill drill, SpeechAccent accent, bool slow)
    {
        var label = slow ? "慢速跟读" : "示范朗读";
        var button = CreateSpeechPill(slow ? "gauge" : "volume-high", label,
            playback => speech.SpeakAsync(drill.Text, accent, ipaPhonetic: null,
                speed: slow ? 0.68f : settings.Current.SpeechSpeed,
                preferredVoiceId: accent == SpeechAccent.AmericanEnglish ? settings.Current.PreferredUsVoiceId : null,
                playback: playback));
        AutomationProperties.SetName(button, $"{label} · {drill.Focus}");
        ToolTip.SetTip(button, $"{label}：{drill.Text}");
        return button;
    }

    private static Button CreateSpeechPill(string icon, string text, Func<SpeechPlayback, Task> speak, SpeechPlayback? playback = null) =>
        SpeechPlaybackButton.Create(click => BuildPill(icon, text, click), speak, playback);

    private static Button BuildPill(string icon, string text, Action onClick)
    {
        var btn = new Button
        {
            Classes = { "ghost" },
            Background = AppTheme.CardElevatedBg,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(AppTheme.Radius.Pill),
            Padding = new Thickness(10, 5, 12, 5),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontAwesomeIcon(icon, 14, AppTheme.TextSecondary),
                    TextStyles.Apply(new TextBlock
                    {
                        Text = text,
                        VerticalAlignment = VerticalAlignment.Center
                    }, TextRole.Phonetic)
                }
            }
        };
        AutomationProperties.SetName(btn, text);
        ToolTip.SetTip(btn, $"播放 · {text}");
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
