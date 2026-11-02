using AiTranslate.Speech;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

/// <summary>
/// Reusable KokoroSharp Phonics & Multi-Accent Speech Bar:
/// - US Pronunciation (`volume-high`, IPA badge)
/// - UK Pronunciation (`volume-high`, IPA badge)
/// - Natural Phonics Syllable Breakdown (`waveform` 自然拼读音节拆解领读)
/// - Slow Shadow-Reading (`speedometer-slow` 0.68x 慢速跟读)
/// - Vocabulary Memory Loop (`repeat` 美音→英音→原句磨耳朵循环读)
/// </summary>
public static class PhonicsAudioBar
{
    public static Control CreateWordPhonicsStrip(
        ISpeechSynthesisService speech,
        AppSettingsStore settings,
        string headword,
        string? usIpa,
        string? ukIpa,
        string? exampleSentence = null,
        bool compact = false)
    {
        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };

        // 1. US Accent Button
        var usLabel = !string.IsNullOrWhiteSpace(usIpa) ? $"美 {usIpa}" : "美音 US";
        wrap.Children.Add(CreateSpeechPill(
            "volume-high",
            usLabel,
            AppTheme.AccentSoft,
            AppTheme.AccentBright,
            () => _ = speech.SpeakAsync(
                headword,
                SpeechAccent.AmericanEnglish,
                usIpa,
                settings.Current.SpeechSpeed,
                settings.Current.PreferredUsVoiceId)));

        // 2. UK Accent Button
        var ukLabel = !string.IsNullOrWhiteSpace(ukIpa) ? $"英 {ukIpa}" : "英音 UK";
        wrap.Children.Add(CreateSpeechPill(
            "volume-high",
            ukLabel,
            AppTheme.PurpleSoft,
            AppTheme.Purple,
            () => _ = speech.SpeakAsync(
                headword,
                SpeechAccent.BritishEnglish,
                ukIpa,
                settings.Current.SpeechSpeed,
                settings.Current.PreferredUkVoiceId)));

        // 3. Phonics Syllable Breakdown (自然拼读拆解领读)
        var syllables = KokoroSpeechService.SplitIntoPhonicsSyllables(headword);
        var syllableHint = syllables.Count > 1 && !compact
            ? $"自然拼读 [{string.Join("·", syllables)}]"
            : "自然拼读";
        wrap.Children.Add(CreateSpeechPill(
            "waveform",
            syllableHint,
            AppTheme.AmberSoft,
            AppTheme.Amber,
            () => _ = speech.SpeakPhonicsBreakdownAsync(headword, usIpa, SpeechAccent.AmericanEnglish)));

        if (!compact)
        {
            // 4. 0.68x Slow Shadow-Reading (慢速精听跟读)
            wrap.Children.Add(CreateSpeechPill(
                "speedometer-slow",
                "0.7x 慢读跟读",
                AppTheme.CardElevatedBg,
                AppTheme.TextSecondary,
                () => _ = speech.SpeakAsync(
                    headword,
                    SpeechAccent.AmericanEnglish,
                    usIpa,
                    speed: 0.68f,
                    settings.Current.PreferredUsVoiceId)));

            // 5. Vocabulary Memory Loop (美音 -> 英音 -> 例句连读)
            wrap.Children.Add(CreateSpeechPill(
                "repeat",
                "磨耳朵连读",
                AppTheme.EmeraldSoft,
                AppTheme.Emerald,
                () => _ = speech.SpeakVocabularyCardAsync(headword, usIpa, ukIpa, exampleSentence)));
        }

        return wrap;
    }

    public static Button CreateSentenceReadButton(
        ISpeechSynthesisService speech,
        AppSettingsStore settings,
        string sentence,
        string label = "朗读例句")
    {
        return CreateSpeechPill(
            "account-voice",
            label,
            AppTheme.AccentSoft,
            AppTheme.AccentBright,
            () => _ = speech.SpeakAsync(
                sentence,
                SpeechAccent.AmericanEnglish,
                ipaPhonetic: null,
                speed: settings.Current.SpeechSpeed,
                preferredVoiceId: settings.Current.PreferredUsVoiceId));
    }

    private static Button CreateSpeechPill(string mdiIcon, string text, IBrush bg, IBrush fg, Action onClick)
    {
        var btn = new Button
        {
            Background = bg,
            BorderBrush = AppTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(11, 5),
            Margin = new Thickness(0, 0, 8, 6),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new MdiIcon(mdiIcon, 14, fg),
                    new TextBlock
                    {
                        Text = text,
                        FontFamily = AppTheme.MonoFont,
                        FontSize = 12,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = fg,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
