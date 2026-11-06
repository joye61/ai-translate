using System.Diagnostics;
using System.Text;
using KokoroSharp;
using KokoroSharp.Core;
using Microsoft.ML.OnnxRuntime;

namespace AiTranslate.Speech;

/// <summary>
/// Next-Gen Neural TTS & Phonics Speech Engine powered by KokoroSharp (Kokoro-82M ONNX)
/// with seamless cross-platform OS fallback (Windows SAPI / macOS `say` / Linux `spd-say` or `espeak-ng`).
/// Features:
/// 1. Authentic American & British Kokoro neural voices + Multi-voice blending (KokoroVoiceManager.Mix)
/// 2. IPA Phoneme Literal Injection (`[word](/ipa/)`) for dictionary-grade pronunciation accuracy
/// 3. Natural Phonics & Syllable Breakdown (自然拼读拆解领读：完整词 -> 音节慢读 -> 字母拼读 -> 完整词)
/// 4. Vocabulary Memory Loop (美音 -> 英音 -> 原句语境磨耳朵连读)
/// </summary>
public sealed partial class KokoroSpeechService : ISpeechSynthesisService
{
    private readonly string _modelDirectory;
    private readonly object _modelGate = new();
    private Task<bool>? _modelLoad;
    private volatile KokoroAudio? _kokoro;
    private volatile bool _disposed;
    private static readonly object VoiceLoading = new();

    public bool IsKokoroModelLoaded => _kokoro is not null;
    public string EngineStatus { get; private set; } = "等待加载离线语音模型";

    public event EventHandler<string>? StatusChanged;

    public static IReadOnlyList<KokoroSpeakerProfile> Catalog { get; } =
    [
        // American English Voices
        new("af_heart", "Heart · 美式女声", SpeechAccent.AmericanEnglish, "Female", "温暖自然 · 旗舰推荐"),
        new("af_bella", "Bella · 美式女声", SpeechAccent.AmericanEnglish, "Female", "清晰知性 · 适合长句精读"),
        new("af_nicole", "Nicole · 美式女声", SpeechAccent.AmericanEnglish, "Female", "柔和沉浸 · 适合听力磨耳朵"),
        new("af_sarah", "Sarah · 美式女声", SpeechAccent.AmericanEnglish, "Female", "新闻播音级发音"),
        new("am_adam", "Adam · 美式男声", SpeechAccent.AmericanEnglish, "Male", "沉稳浑厚 · 美式标准音"),
        new("am_michael", "Michael · 美式男声", SpeechAccent.AmericanEnglish, "Male", "清晰学者风范"),
        new("am_fenrir", "Fenrir · 美式男声", SpeechAccent.AmericanEnglish, "Male", "富有表现力"),

        // British English Voices
        new("bf_emma", "Emma · 英式女声 (RP)", SpeechAccent.BritishEnglish, "Female", "纯正伦敦腔 · 雅思推荐"),
        new("bf_isabella", "Isabella · 英式女声", SpeechAccent.BritishEnglish, "Female", "优雅学院风"),
        new("bf_alice", "Alice · 英式女声", SpeechAccent.BritishEnglish, "Female", "轻快清晰"),
        new("bm_george", "George · 英式男声 (RP)", SpeechAccent.BritishEnglish, "Male", "经典 BBC 播音腔"),
        new("bm_lewis", "Lewis · 英式男声", SpeechAccent.BritishEnglish, "Male", "自然绅士腔"),
        new("bm_daniel", "Daniel · 英式男声", SpeechAccent.BritishEnglish, "Male", "沉稳叙事风"),

        // Mandarin & Japanese Voices bundled in Kokoro-82M
        new("zf_xiaoxiao", "Xiaoxiao · 中文女声", SpeechAccent.MandarinChinese, "Female", "自然普通话"),
        new("jf_alpha", "Alpha · 日语女声", SpeechAccent.Japanese, "Female", "标准东京音")
    ];

    public KokoroSpeechService(string? modelDirectory = null)
    {
        _modelDirectory = modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "KokoroModels");
    }

    public IReadOnlyList<KokoroSpeakerProfile> GetAvailableVoices(SpeechAccent? accentFilter = null) =>
        accentFilter is null
            ? Catalog
            : Catalog.Where(v => v.Accent == accentFilter.Value).ToArray();

    public Task<bool> EnsureKokoroModelReadyAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<bool>(cancellationToken);
        lock (_modelGate)
        {
            if (_disposed) return Task.FromResult(false);
            if (_kokoro is not null) return Task.FromResult(true);
            if (_modelLoad is null || _modelLoad.IsCompleted)
                _modelLoad = Task.Run(LoadBundledModel);
            // Stopping a reading cancels its wait, not the model shared by the application.
            return _modelLoad.WaitAsync(cancellationToken);
        }
    }

    public async Task PreparePronunciationAsync(string text, SpeechAccent accent, string? ipa, float speed, string? voiceId,
        CancellationToken token)
    {
        Task<bool>? preload;
        lock (_modelGate) preload = _modelLoad;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 48 || preload is null || _disposed) return;
        try
        {
            if (await preload.WaitAsync(token).ConfigureAwait(false) && _kokoro is { } engine)
                await Task.Run(() => engine.Prepare(FormatKokoroIpaInput(text, ipa), ResolveKokoroVoice(accent, voiceId),
                    Math.Clamp(speed, .5f, 1.8f), Idle, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Debug.WriteLine(ex); } // Playback has its own system-voice fallback.

        bool Idle() { lock (_speechGate) return !_disposed && _reading is null; }
    }

    private bool LoadBundledModel()
    {
        KokoroAudio? engine = null;
        try
        {
            if (_disposed) return false;
            UpdateStatus($"正在后台加载{ISpeechSynthesisService.DisplayName}…");
            var model = Path.Combine(_modelDirectory, "kokoro.onnx");
            if (!File.Exists(model))
            {
                UpdateStatus("缺少离线语音模型，朗读将使用系统语音。");
                return false;
            }
            engine = LoadKokoroFromPath(model);
            // Loading weights alone leaves the tokenizer and the first ONNX run on the first click.
            // An early reading does that work itself rather than waiting behind a warm-up clip.
            bool warmUp;
            lock (_speechGate) warmUp = _reading is null;
            if (warmUp && !_disposed) engine.WarmUp(ResolveKokoroVoice(SpeechAccent.AmericanEnglish, null));
            lock (_modelGate)
            {
                if (_disposed) return false;
                _kokoro = engine;
                engine = null; // Dispose now owns the published engine.
            }
            UpdateStatus($"{ISpeechSynthesisService.DisplayName}已就绪，可离线朗读。");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            UpdateStatus("离线语音模型加载失败，朗读将使用系统语音。");
            return false;
        }
        finally
        {
            engine?.Dispose();
        }
    }

    private static KokoroAudio LoadKokoroFromPath(string modelPath)
    {
        var voicesDir = Path.Combine(AppContext.BaseDirectory, "voices");
        lock (VoiceLoading)
            if (KokoroVoiceManager.Voices.Count == 0) KokoroVoiceManager.LoadVoicesFromPath(voicesDir);

        using var sessionOptions = CreateOptimalSessionOptions();
        return new KokoroAudio(modelPath, sessionOptions);
    }

    private static SessionOptions CreateOptimalSessionOptions()
    {
        var opts = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        opts.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        opts.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        return opts;
    }

    /// <summary>
    /// Uses KokoroSharp's native Phoneme Literal syntax `[word](/ipa/)` when a single word and its IPA are provided,
    /// guaranteeing exact dictionary pronunciation.
    /// </summary>
    public static string FormatKokoroIpaInput(string text, string? ipaPhonetic)
    {
        if (string.IsNullOrWhiteSpace(ipaPhonetic)) return text;
        var trimmed = text.Trim();
        if (trimmed.Contains(' ') || trimmed.Contains('\n')) return trimmed;

        var cleanIpa = ipaPhonetic.Trim().Trim('/', '[', ']');
        if (string.IsNullOrWhiteSpace(cleanIpa)) return trimmed;
        return $"[{trimmed}](/{cleanIpa}/)";
    }

    /// <summary>
    /// Splits an English word into natural phonics syllables for visual and audio syllable breakdown.
    /// </summary>
    public static IReadOnlyList<string> SplitIntoPhonicsSyllables(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return [];
        var clean = word.Trim();
        if (clean.Length <= 3 || clean.Contains(' ')) return [clean];

        static bool IsVowel(char c) => "aeiouyAEIOUY".Contains(c);

        var syllables = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < clean.Length; i++)
        {
            current.Append(clean[i]);
            var hasVowel = current.ToString().Any(IsVowel);
            if (!hasVowel || i >= clean.Length - 2) continue;

            var nextIsConsonant = !IsVowel(clean[i + 1]);
            var afterNextIsVowel = i + 2 < clean.Length && IsVowel(clean[i + 2]);
            var twoConsonantsBetweenVowels = nextIsConsonant && i + 2 < clean.Length && !IsVowel(clean[i + 2]) && i + 3 < clean.Length && IsVowel(clean[i + 3]);

            if ((IsVowel(clean[i]) && nextIsConsonant && afterNextIsVowel && current.Length >= 2) ||
                ( !IsVowel(clean[i]) && twoConsonantsBetweenVowels))
            {
                syllables.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            syllables.Add(current.ToString());

        return syllables.Count == 0 ? [clean] : syllables;
    }

    private static KokoroVoice ResolveKokoroVoice(SpeechAccent accent, string? preferredVoiceId)
    {
        var defaultId = accent switch
        {
            SpeechAccent.BritishEnglish => "bf_emma",
            SpeechAccent.MandarinChinese => "zf_xiaoxiao",
            SpeechAccent.Japanese => "jf_alpha",
            _ => "af_heart"
        };
        return KokoroVoiceManager.Voices.FirstOrDefault(v => string.Equals(v.Name, preferredVoiceId, StringComparison.OrdinalIgnoreCase))
            ?? KokoroVoiceManager.GetVoice(defaultId);
    }

    private void UpdateStatus(string status)
    {
        if (_disposed) return;
        EngineStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        lock (_modelGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        var engine = _kokoro;
        _kokoro = null;
        engine?.Dispose();
    }
}
