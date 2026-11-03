using System.Text.Json.Serialization;

namespace AiTranslate.Core;

/// <summary>
/// Application-wide settings persisted locally, covering AI provider, translation defaults, and hotkeys.
/// </summary>
public sealed record AppSettings
{
    public string ProviderId { get; init; } = "deepseek";
    public string ModelId { get; init; } = "deepseek-chat";
    public string? CustomBaseUrl { get; init; }
    public string ApiKey { get; init; } = "";

    public string DefaultTargetLanguage { get; init; } = "zh-Hans";
    public string? DictionaryTargetMode { get; init; }
    public string SecondaryTargetLanguage { get; init; } = "en";

    public bool EnableClipboardMonitor { get; init; }
    public bool EnableMouseSelectionTranslate { get; init; }
    public bool AutoAddLookedUpWordsToNotebook { get; init; }
    public string DefaultNotebookId { get; init; } = "default";

    // KokoroSharp Neural Speech & Phonics Configuration
    public string PreferredUsVoiceId { get; init; } = "af_heart";
    public string PreferredUkVoiceId { get; init; } = "bf_emma";
    public bool EnableDualVoiceBlend { get; init; }
    public string SecondaryBlendVoiceId { get; init; } = "af_nicole";
    public float SpeechSpeed { get; init; } = 1.0f;
    public bool AutoPronounceOnLookup { get; init; }

    public HotkeyGesture ShowWindowHotkey { get; init; } = new("D", HotkeyModifiers.Alt);
    public HotkeyGesture SelectionTranslateHotkey { get; init; } = new("Q", HotkeyModifiers.Alt);
    public HotkeyGesture ScreenshotTranslateHotkey { get; init; } = new("S", HotkeyModifiers.Alt);
    public HotkeyGesture ClipboardTranslateHotkey { get; init; } = new("W", HotkeyModifiers.Alt);
}
