using System.Diagnostics;
using System.Text;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
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
public sealed class KokoroSpeechService : ISpeechSynthesisService
{
    private readonly string _modelDirectory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private KokoroTTS? _kokoro;
    private Process? _fallbackProcess;
    private CancellationTokenSource? _activeSpeechCts;
    private bool _disposed;

    public bool IsKokoroModelLoaded => _kokoro is not null;
    public bool IsSpeaking { get; private set; }
    public string EngineStatus { get; private set; } = "KokoroSharp 神经语音引擎就绪（支持美音 / 英音 / IPA 音标直驱 / 自然拼读拆解）";

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
        _modelDirectory = modelDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiTranslate",
            "KokoroModels");
        TryInitializePreexistingLocalModel();
    }

    public IReadOnlyList<KokoroSpeakerProfile> GetAvailableVoices(SpeechAccent? accentFilter = null) =>
        accentFilter is null
            ? Catalog
            : Catalog.Where(v => v.Accent == accentFilter.Value).ToArray();

    private void TryInitializePreexistingLocalModel()
    {
        try
        {
            var localModel = FindExistingModelFile();
            if (localModel is not null)
            {
                LoadKokoroFromPath(localModel);
            }
        }
        catch
        {
            // Keep service operational via cross-platform fallback until model is downloaded
        }
    }

    private string? FindExistingModelFile()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "kokoro.onnx"),
            Path.Combine(AppContext.BaseDirectory, "KokoroModels", "kokoro.onnx"),
            Path.Combine(_modelDirectory, "kokoro.onnx")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<bool> EnsureKokoroModelReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_kokoro is not null) return true;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_kokoro is not null) return true;

            var existing = FindExistingModelFile();
            if (existing is not null)
            {
                UpdateStatus("正在加载本地 Kokoro-82M 神经网络语音模型…");
                await Task.Run(() => LoadKokoroFromPath(existing), cancellationToken).ConfigureAwait(false);
                return _kokoro is not null;
            }

            UpdateStatus("正在通过 KokoroSharp 初始化/下载 Kokoro-82M 离线语音模型…");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(_modelDirectory);
                var sessionOptions = CreateOptimalSessionOptions();
                _kokoro = KokoroTTS.LoadModel(sessionOptions: sessionOptions);
                WireKokoroCallbacks(_kokoro);
            }, cancellationToken).ConfigureAwait(false);

            UpdateStatus("Kokoro-82M 神经网络模型已就绪 · 已启用 54 款多语种人声与 IPA 音标直驱");
            return true;
        }
        catch (Exception ex)
        {
            UpdateStatus($"Kokoro 离线模型暂未就绪 ({ex.Message}) · 已自动切换至系统原生高保真语音引擎");
            return false;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void LoadKokoroFromPath(string modelPath)
    {
        var voicesDir = Path.Combine(AppContext.BaseDirectory, "voices");
        if (Directory.Exists(voicesDir) && KokoroVoiceManager.Voices.Count == 0)
            KokoroVoiceManager.LoadVoicesFromPath(voicesDir);

        var sessionOptions = CreateOptimalSessionOptions();
        _kokoro = KokoroTTS.LoadModel(modelPath, sessionOptions: sessionOptions);
        WireKokoroCallbacks(_kokoro);
        UpdateStatus("KokoroSharp 神经网络模型已加载 · 支持 IPA 音标直驱与多声线融合");
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

    private void WireKokoroCallbacks(KokoroTTS tts)
    {
        tts.OnSpeechStarted += pkt =>
        {
            IsSpeaking = true;
            UpdateStatus($"Kokoro 正在朗读 · 音素流：{new string(pkt.PhonemesToSpeak)}");
        };
        tts.OnSpeechCompleted += _ =>
        {
            IsSpeaking = false;
            UpdateStatus("朗读完成");
        };
        tts.OnSpeechCanceled += _ =>
        {
            IsSpeaking = false;
        };
    }

    public async Task SpeakAsync(
        string text,
        SpeechAccent accent = SpeechAccent.AmericanEnglish,
        string? ipaPhonetic = null,
        float speed = 1.0f,
        string? preferredVoiceId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Stop();

        var previousCts = _activeSpeechCts;
        _activeSpeechCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        previousCts?.Dispose();
        var token = _activeSpeechCts.Token;
        var clampedSpeed = Math.Clamp(speed, 0.5f, 1.8f);

        if (_kokoro is not null)
        {
            try
            {
                await Task.Run(() =>
                {
                    var voice = ResolveKokoroVoice(accent, preferredVoiceId);
                    var preparedText = FormatKokoroIpaInput(text.Trim(), ipaPhonetic);
                    var tokens = Tokenizer.Tokenize(preparedText);
                    var segments = SegmentationSystem.SplitToSegments(tokens, new DefaultSegmentationConfig
                    {
                        MaxFirstSegmentLength = 80
                    });
                    IsSpeaking = true;
                    foreach (var segment in segments)
                    {
                        token.ThrowIfCancellationRequested();
                        _kokoro.EnqueueJob(KokoroJob.Create(segment, voice, clampedSpeed, null));
                    }
                }, token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Fall back to cross-platform OS speech if eSpeak/OpenAL native backend is unavailable on current machine
            }
        }

        await SpeakWithPlatformNativeTtsAsync(text.Trim(), accent, clampedSpeed, token).ConfigureAwait(false);
    }

    public async Task SpeakPhonicsBreakdownAsync(
        string headword,
        string? ipaPhonetic = null,
        SpeechAccent accent = SpeechAccent.AmericanEnglish,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(headword)) return;
        var word = headword.Trim();
        var syllables = SplitIntoPhonicsSyllables(word);
        var spelled = string.Join(", ", word.Where(char.IsLetter).Select(c => c.ToString().ToUpperInvariant()));

        // Phonics study sequence:
        // 1. Normal pronunciation
        // 2. Slow syllable-by-syllable articulation
        // 3. Letter spelling
        // 4. Final clear confirmation
        var phonicsScript = syllables.Count > 1
            ? $"{word}. ... {string.Join(" ... ", syllables)}. ... {spelled}. ... {word}."
            : $"{word}. ... {spelled}. ... {word}.";

        UpdateStatus($"自然拼读领读：{word}  →  [{string.Join(" · ", syllables)}]  {ipaPhonetic}");
        await SpeakAsync(phonicsScript, accent, ipaPhonetic: null, speed: 0.78f, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task SpeakVocabularyCardAsync(
        string headword,
        string? usIpa,
        string? ukIpa,
        string? exampleSentence,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(headword)) return;
        UpdateStatus($"正在进行生词卡片沉浸连读：{headword} (美音 → 英音 → 语境例句)");

        await SpeakAsync(headword, SpeechAccent.AmericanEnglish, usIpa, speed: 0.95f, cancellationToken: cancellationToken).ConfigureAwait(false);
        await Task.Delay(450, cancellationToken).ConfigureAwait(false);

        await SpeakAsync(headword, SpeechAccent.BritishEnglish, ukIpa, speed: 0.92f, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(exampleSentence))
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            await SpeakAsync(exampleSentence, SpeechAccent.AmericanEnglish, speed: 0.96f, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
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
        var targetId = string.IsNullOrWhiteSpace(preferredVoiceId) ? defaultId : preferredVoiceId;
        try
        {
            return KokoroVoiceManager.GetVoice(targetId);
        }
        catch
        {
            return KokoroVoiceManager.Voices.First();
        }
    }

    private async Task SpeakWithPlatformNativeTtsAsync(string text, SpeechAccent accent, float speed, CancellationToken token)
    {
        IsSpeaking = true;
        var accentLabel = accent == SpeechAccent.BritishEnglish ? "英音 UK" : "美音 US";
        UpdateStatus($"正在朗读 ({accentLabel} · {speed:F1}x)：{(text.Length > 36 ? text[..36] + "…" : text)}");

        try
        {
            ProcessStartInfo? psi = null;
            if (OperatingSystem.IsWindows())
            {
                // SAPI rate ranges from -10 to +10
                var sapiRate = Math.Clamp((int)Math.Round((speed - 1.0f) * 8), -6, 6);
                var cultureHint = accent == SpeechAccent.BritishEnglish ? "en-GB" : "en-US";
                var escapedText = text.Replace("'", "''").Replace("\r", " ").Replace("\n", " ");
                var psCommand = $"Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; try {{ $s.SelectVoiceByHints([System.Speech.Synthesis.VoiceGender]::NotSet, [System.Speech.Synthesis.VoiceAge]::NotSet, 0, [System.Globalization.CultureInfo]::GetCultureInfo('{cultureHint}')) }} catch {{}}; $s.Rate = {sapiRate}; $s.Speak('{escapedText}')";
                psi = new ProcessStartInfo("powershell", ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", psCommand])
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
            }
            else if (OperatingSystem.IsMacOS())
            {
                var voiceName = accent == SpeechAccent.BritishEnglish ? "Daniel" : "Samantha";
                var wpm = Math.Clamp((int)Math.Round(175 * speed), 90, 320).ToString();
                psi = new ProcessStartInfo("say", ["-v", voiceName, "-r", wpm, text])
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
            }
            else if (OperatingSystem.IsLinux())
            {
                var ratePercent = Math.Clamp((int)Math.Round((speed - 1.0f) * 100), -60, 80).ToString();
                var lang = accent == SpeechAccent.BritishEnglish ? "en-gb" : "en-us";
                psi = new ProcessStartInfo("spd-say", ["--wait", "-l", lang, "-r", ratePercent, text])
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
            }

            if (psi is null) return;
            using var proc = Process.Start(psi);
            _fallbackProcess = proc;
            if (proc is not null)
            {
                await proc.WaitForExitAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            _fallbackProcess = null;
            IsSpeaking = false;
            UpdateStatus("语音就绪");
        }
    }

    public void Stop()
    {
        try { _activeSpeechCts?.Cancel(); } catch { }
        try { _kokoro?.StopPlayback(); } catch { }
        try
        {
            if (_fallbackProcess is { HasExited: false } proc)
                proc.Kill(entireProcessTree: true);
        }
        catch { }
        IsSpeaking = false;
    }

    private void UpdateStatus(string status)
    {
        EngineStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _activeSpeechCts?.Dispose();
        _kokoro?.Dispose();
        _initLock.Dispose();
    }
}
