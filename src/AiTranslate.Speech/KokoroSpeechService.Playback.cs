using System.Diagnostics;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;

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
        public KokoroJob? Job { get; set; }
        public TaskCompletionSource? Completion { get; set; }
    }

    public bool IsSpeaking
    {
        get { lock (_speechGate) return _reading?.Playback.IsPlaying == true; }
    }

    private void WireKokoroCallbacks(KokoroTTS tts)
    {
        tts.OnSpeechStarted += packet =>
        {
            lock (_speechGate)
            {
                if (_reading is not { } reading || reading.Job != packet.RelatedJob || reading.Token.IsCancellationRequested) return;
                reading.Playback.SetPlaying(true);
                UpdateStatus($"{ISpeechSynthesisService.DisplayName}正在朗读");
            }
        };
        tts.OnSpeechCompleted += packet =>
        {
            lock (_speechGate)
                if (_reading?.Job == packet.RelatedJob) _reading?.Completion?.TrySetResult();
        };
        tts.OnSpeechCanceled += packet =>
        {
            lock (_speechGate)
                if (_reading?.Job == packet.RelatedJob) _reading?.Completion?.TrySetCanceled();
        };
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
        }
        try
        {
            await speak(reading).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
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
                        reading.Playback.SetPlaying(false);
                        UpdateStatus(reading.Job is null ? "语音就绪" : "朗读完成");
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
        if (await EnsureKokoroModelReadyAsync(token).ConfigureAwait(false))
        {
            try
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await Task.Run(() =>
                {
                    var voice = ResolveKokoroVoice(accent, voiceId);
                    var prepared = FormatKokoroIpaInput(text, ipa);
                    lock (_speechGate)
                    {
                        token.ThrowIfCancellationRequested();
                        reading.Job = _kokoro!.Speak(prepared, voice, new KokoroTTSPipelineConfig { Speed = clampedSpeed }).Job;
                        reading.Completion = completion;
                    }
                }, token).ConfigureAwait(false);
                // Speak only enqueues synthesis. The matching audio callback finishes this part.
                await completion.Task.WaitAsync(token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
        try
        {
            ProcessStartInfo? psi = null;
            if (OperatingSystem.IsWindows())
            {
                // SAPI rate ranges from -10 to +10
                var sapiRate = Math.Clamp((int)Math.Round((speed - 1.0f) * 8), -6, 6);
                var cultureHint = accent == SpeechAccent.BritishEnglish ? "en-GB" : "en-US";
                var psCommand = $"Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; try {{ $s.SelectVoiceByHints([System.Speech.Synthesis.VoiceGender]::NotSet, [System.Speech.Synthesis.VoiceAge]::NotSet, 0, [System.Globalization.CultureInfo]::GetCultureInfo('{cultureHint}')) }} catch {{}}; $s.Rate = {sapiRate}; [Console]::WriteLine('ready'); $s.Speak($env:AI_TRANSLATE_SPEECH_TEXT)";
                psi = new ProcessStartInfo("powershell", ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", psCommand])
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                psi.Environment["AI_TRANSLATE_SPEECH_TEXT"] = text;
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
            if (proc is null) return;
            using var stop = reading.Token.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // Process already exited.
                catch (System.ComponentModel.Win32Exception ex) { Debug.WriteLine(ex); }
            });
            if (psi.RedirectStandardOutput && await proc.StandardOutput.ReadLineAsync(reading.Token).ConfigureAwait(false) != "ready") return;
            lock (_speechGate)
            {
                reading.Token.ThrowIfCancellationRequested();
                reading.Playback.SetPlaying(true);
                UpdateStatus($"系统语音正在朗读 · {speed:F1}x");
            }
            await proc.WaitForExitAsync(reading.Token).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex) { Debug.WriteLine(ex); }
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
        reading.Playback.SetPlaying(false);
        if (reading.Job is not null)
        {
            try { _kokoro?.StopPlayback(); }
            catch (InvalidOperationException ex) { Debug.WriteLine(ex); }
        }
    }
}
