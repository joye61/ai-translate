using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AiTranslate.Speech;

public sealed partial class KokoroSpeechService
{
    private readonly object _speechGate = new();
    private Reading? _reading;

    private sealed class Reading(CancellationTokenSource cancellation, SpeechPlayback playback)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationToken Token { get; } = cancellation.Token;
        public SpeechPlayback Playback { get; } = playback;
    }

    public bool IsSpeaking
    {
        get { lock (_speechGate) return _reading?.Playback.IsPlaying == true; }
    }

    private void Started(Reading reading, string status)
    {
        lock (_speechGate)
        {
            if (_reading != reading || reading.Token.IsCancellationRequested) return;
            reading.Playback.SetState(SpeechPlaybackState.Playing);
            UpdateStatus(status);
        }
    }

    public Task SpeakAsync(string text, SpeechAccent accent = SpeechAccent.AmericanEnglish,
        string? ipaPhonetic = null, float speed = 1.0f, string? preferredVoiceId = null,
        CancellationToken cancellationToken = default, SpeechPlayback? playback = null) =>
        string.IsNullOrWhiteSpace(text) ? Task.CompletedTask : RunReadingAsync(
            reading => SpeakPartAsync(reading, text.Trim(), accent, ipaPhonetic, speed, preferredVoiceId),
            cancellationToken, playback);

    public async Task SpeakPhonicsBreakdownAsync(
        string headword,
        string? ipaPhonetic = null,
        SpeechAccent accent = SpeechAccent.AmericanEnglish,
        CancellationToken cancellationToken = default,
        SpeechPlayback? playback = null)
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
        await SpeakAsync(phonicsScript, accent, ipaPhonetic: null, speed: 0.78f, cancellationToken: cancellationToken, playback: playback).ConfigureAwait(false);
    }

    public Task SpeakVocabularyCardAsync(string headword, string? usIpa, string? ukIpa, string? exampleSentence,
        CancellationToken cancellationToken = default, SpeechPlayback? playback = null)
    {
        if (string.IsNullOrWhiteSpace(headword)) return Task.CompletedTask;
        return RunReadingAsync(async reading =>
        {
            await SpeakPartAsync(reading, headword, SpeechAccent.AmericanEnglish, usIpa, .95f).ConfigureAwait(false);
            await Task.Delay(450, reading.Token).ConfigureAwait(false);
            await SpeakPartAsync(reading, headword, SpeechAccent.BritishEnglish, ukIpa, .92f).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(exampleSentence))
            {
                await Task.Delay(500, reading.Token).ConfigureAwait(false);
                await SpeakPartAsync(reading, exampleSentence, SpeechAccent.AmericanEnglish, null, .96f).ConfigureAwait(false);
            }
        }, cancellationToken, playback);
    }

    private async Task RunReadingAsync(Func<Reading, Task> speak, CancellationToken token, SpeechPlayback? playback)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reading = new Reading(cancellation, playback ?? new SpeechPlayback());
        lock (_speechGate)
        {
            if (_disposed || token.IsCancellationRequested) return;
            StopLocked();
            _reading = reading;
            reading.Playback.SetState(SpeechPlaybackState.Preparing);
        }
        var completed = false;
        try
        {
            // Even a fully cached clip must open its audio device off the UI thread.
            await Task.Run(() => speak(reading), reading.Token).ConfigureAwait(false);
            completed = true;
        }
        catch (OperationCanceledException) when (reading.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            lock (_speechGate)
                if (_reading == reading) UpdateStatus("朗读失败，请检查音频输出设备。");
        }
        finally
        {
            lock (_speechGate)
            {
                if (_reading == reading)
                {
                    if (reading.Token.IsCancellationRequested) StopLocked();
                    else
                    {
                        _reading = null;
                        var played = reading.Playback.IsPlaying;
                        reading.Playback.SetState(SpeechPlaybackState.Idle);
                        if (played && completed) UpdateStatus("朗读完成");
                    }
                }
            }
        }
    }

    private async Task SpeakPartAsync(Reading reading, string text, SpeechAccent accent, string? ipa, float speed,
        string? voiceId = null)
    {
        var token = reading.Token;
        var clampedSpeed = Math.Clamp(speed, .5f, 1.8f);
        // An explicit preload retries a failed model. Subsequent readings can use system speech immediately.
        Task<bool> modelReady;
        lock (_modelGate) modelReady = _modelLoad?.WaitAsync(token) ?? EnsureKokoroModelReadyAsync(token);
        if (await modelReady.ConfigureAwait(false) && _kokoro is { } engine)
        {
            var began = false;
            try
            {
                token.ThrowIfCancellationRequested();
                await engine.SpeakAsync(FormatKokoroIpaInput(text, ipa), ResolveKokoroVoice(accent, voiceId), clampedSpeed, () =>
                {
                    began = true;
                    Started(reading, $"{ISpeechSynthesisService.DisplayName}正在朗读");
                }, token).ConfigureAwait(false);
                if (began) return;
            }
            catch (Exception ex) when (!began && !token.IsCancellationRequested && ex is not OperationCanceledException)
            {
                Debug.WriteLine(ex);
                UpdateStatus("离线朗读暂不可用，正在切换至系统语音。");
            }
        }
        token.ThrowIfCancellationRequested();
        await SpeakWithPlatformNativeTtsAsync(reading, text, accent, clampedSpeed).ConfigureAwait(false);
    }

    private async Task SpeakWithPlatformNativeTtsAsync(Reading reading, string text, SpeechAccent accent, float speed)
    {
        ProcessStartInfo? psi = null;
        if (OperatingSystem.IsWindows())
        {
            SpeakWithSapi(reading, text, accent, speed);
            return;
        }
        else if (OperatingSystem.IsMacOS())
        {
            var voiceName = accent switch { SpeechAccent.BritishEnglish => "Daniel", SpeechAccent.MandarinChinese => "Tingting",
                SpeechAccent.Japanese => "Kyoko", _ => "Samantha" };
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
            var lang = accent switch { SpeechAccent.BritishEnglish => "en-gb", SpeechAccent.MandarinChinese => "zh",
                SpeechAccent.Japanese => "ja", _ => "en-us" };
            psi = new ProcessStartInfo("spd-say", ["--wait", "-l", lang, "-r", ratePercent, text])
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
        }

        if (psi is null) return;
        using var proc = Process.Start(psi);
        if (proc is null) return;
        using var stop = reading.Token.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // Process already exited.
            catch (System.ComponentModel.Win32Exception ex) { Debug.WriteLine(ex); }
        });
        Started(reading, $"系统语音正在朗读 · {speed:F1}x");
        await proc.WaitForExitAsync(reading.Token).ConfigureAwait(false);
        if (proc.ExitCode != 0) throw new InvalidOperationException("The system speech engine could not play this text.");
    }

    [SupportedOSPlatform("windows")]
    private void SpeakWithSapi(Reading reading, string text, SpeechAccent accent, float speed)
    {
        var language = accent switch { SpeechAccent.BritishEnglish => "809", SpeechAccent.MandarinChinese => "804",
            SpeechAccent.Japanese => "411", _ => "409" };
        dynamic voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice", throwOnError: true)!)!;
        try
        {
            dynamic voices = voice.GetVoices("Language=" + language, "");
            try
            {
                if (voices.Count > 0)
                {
                    dynamic preferred = voices.Item(0);
                    try { voice.Voice = preferred; }
                    finally { Marshal.ReleaseComObject(preferred); }
                }
            }
            finally { Marshal.ReleaseComObject(voices); }
            voice.Rate = Math.Clamp((int)Math.Round((speed - 1) * 8), -6, 6);
            reading.Token.ThrowIfCancellationRequested();
            voice.Speak(text, 1 | 16); // Asynchronous, literal text (never SAPI XML).
            Started(reading, $"系统语音正在朗读 · {speed:F1}x");
            while (!voice.WaitUntilDone(20)) reading.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            try { if (reading.Token.IsCancellationRequested) voice.Speak("", 1 | 2); } // Purge pending speech.
            finally { Marshal.ReleaseComObject(voice); }
        }
    }

    public void Stop()
    {
        lock (_speechGate) StopLocked();
    }

    private void StopLocked()
    {
        if (_reading is not { } reading) return;
        _reading = null;
        reading.Cancellation.Cancel();
        reading.Playback.SetState(SpeechPlaybackState.Idle);
    }
}
