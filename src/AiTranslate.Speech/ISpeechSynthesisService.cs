namespace AiTranslate.Speech;

public enum SpeechAccent
{
    AmericanEnglish = 0,
    BritishEnglish = 1,
    MandarinChinese = 2,
    Japanese = 3
}

public enum SpeechReadingMode
{
    /// <summary>Normal natural speed (1.0x).</summary>
    Natural = 0,
    /// <summary>Slow deliberate articulation (0.68x) for shadow-reading and phonetic study.</summary>
    SlowShadowing = 1,
    /// <summary>Syllable-by-syllable / Phonics breakdown -> Spell letters -> Full word.</summary>
    PhonicsBreakdown = 2,
    /// <summary>Vocabulary Memory Loop: Word (US) -> Word (UK) -> Chinese meaning -> Example sentence.</summary>
    VocabularyMemoryLoop = 3
}

public sealed record KokoroSpeakerProfile(
    string VoiceId,
    string DisplayName,
    SpeechAccent Accent,
    string Gender,
    string Description)
{
    public override string ToString() => $"{DisplayName} ({Description})";
}

public enum SpeechPlaybackState { Idle, Preparing, Playing }

/// <summary>Identifies a reading action across manual playback, autoplay and view reattachment.</summary>
public sealed class SpeechPlayback
{
    private volatile SpeechPlaybackState _state;
    public SpeechPlaybackState State => _state;
    public bool IsPlaying => _state == SpeechPlaybackState.Playing;
    public event EventHandler? Changed;

    internal void SetState(SpeechPlaybackState value)
    {
        if (_state == value) return;
        _state = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public interface ISpeechSynthesisService : IDisposable
{
    public const string DisplayName = "神经语音引擎";
    bool IsKokoroModelLoaded { get; }
    bool IsSpeaking { get; }
    string EngineStatus { get; }
    event EventHandler<string>? StatusChanged;

    IReadOnlyList<KokoroSpeakerProfile> GetAvailableVoices(SpeechAccent? accentFilter = null);

    /// <summary>Loads the bundled Kokoro-82M ONNX model from disk.</summary>
    Task<bool> EnsureKokoroModelReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>Optionally prepare a visible headword's opening audio without starting playback.</summary>
    Task PreparePronunciationAsync(string text, SpeechAccent accent, string? ipa, float speed, string? voiceId,
        CancellationToken token) => Task.CompletedTask;

    /// <summary>Speaks a word, phrase, or sentence using the specified accent, IPA phoneme literal, and speed.</summary>
    Task SpeakAsync(
        string text,
        SpeechAccent accent = SpeechAccent.AmericanEnglish,
        string? ipaPhonetic = null,
        float speed = 1.0f,
        string? preferredVoiceId = null,
        CancellationToken cancellationToken = default,
        SpeechPlayback? playback = null);

    /// <summary>
    /// Phonics Breakdown reading (自然拼读模式):
    /// Splits the word into syllables, reads slowly, spells out key letters, then pronounces the full word clearly.
    /// </summary>
    Task SpeakPhonicsBreakdownAsync(
        string headword,
        string? ipaPhonetic = null,
        SpeechAccent accent = SpeechAccent.AmericanEnglish,
        CancellationToken cancellationToken = default,
        SpeechPlayback? playback = null);

    /// <summary>
    /// Immersive Vocabulary Memory Loop (单词磨耳朵循环读):
    /// Reads Headword (US) -> Headword (UK) -> Example Sentence.
    /// </summary>
    Task SpeakVocabularyCardAsync(
        string headword,
        string? usIpa,
        string? ukIpa,
        string? exampleSentence,
        CancellationToken cancellationToken = default,
        SpeechPlayback? playback = null);

    void Stop();
}
