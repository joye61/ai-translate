using System.Runtime.InteropServices;
using System.Threading.Channels;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;

namespace AiTranslate.Speech;

/// <summary>Feeds short, complete phrases to the output as soon as their PCM is available.</summary>
internal sealed class KokoroAudio(string modelPath, SessionOptions options) : IDisposable
{
    private readonly KokoroModel _model = new(modelPath, options);
    // eSpeak uses process-wide language state. Two replacement requests must not phonemize concurrently.
    private static readonly object Phonemizer = new();
    private readonly object _inference = new();
    private readonly SemaphoreSlim _speaker = new(1, 1);
    private readonly Dictionary<ClipKey, byte[]> _clips = [];
    private readonly Queue<ClipKey> _arrival = new();
    private const int CacheBytes = 8 * 1024 * 1024;
    private int _cacheSize;
    private readonly record struct ClipKey(string Voice, float Speed, string Phonemes);

    public void WarmUp(KokoroVoice voice) => Render(Tokenize("a", voice), voice, 1, CancellationToken.None);

    public void Prepare(string text, KokoroVoice voice, float speed, Func<bool> idle, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!idle()) return;
        if (Segments(Tokenize(text, voice)).FirstOrDefault() is { } first)
            Render(first, voice, speed, token, idle);
    }

    public async Task SpeakAsync(string text, KokoroVoice voice, float speed, Action started, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
        var producer = Task.Run(async () =>
        {
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var tokens = Tokenize(text, voice);
                foreach (var segment in Segments(tokens))
                {
                    var pcm = Render(segment, voice, speed, lifetime.Token);
                    if (pcm.Length > 0) await chunks.Writer.WriteAsync(pcm, lifetime.Token).ConfigureAwait(false);
                }
                chunks.Writer.TryComplete();
            }
            catch (Exception ex) { chunks.Writer.TryComplete(ex); }
        });
        try
        {
            // Start opening the selected output device while the first phrase is synthesized.
            await _speaker.WaitAsync(token).ConfigureAwait(false);
            try { await SpeechPcmOutput.PlayAsync(chunks.Reader, started, token).ConfigureAwait(false); }
            finally { _speaker.Release(); }
        }
        finally
        {
            lifetime.Cancel();
            await producer.ConfigureAwait(false);
        }
    }

    private static int[] Tokenize(string text, KokoroVoice voice)
    {
        lock (Phonemizer) return Tokenizer.Tokenize(text, voice.GetLangCode());
    }

    // Split at punctuation or word boundaries. Bound even unpunctuated input so ONNX never truncates it.
    internal static IEnumerable<int[]> Segments(int[] tokens)
    {
        var space = Tokenizer.Vocab[' '];
        for (var start = 0; start < tokens.Length;)
        {
            var limit = Math.Min(start == 0 ? 24 : 80, tokens.Length - start);
            var end = start + limit;
            if (end < tokens.Length)
            {
                var boundary = Array.FindLastIndex(tokens, end - 1, limit, Tokenizer.PunctuationTokens.Contains);
                if (boundary < start) boundary = Array.LastIndexOf(tokens, space, end - 1, limit);
                // Allow a long word to finish, but keep in-flight inference short enough to interrupt.
                if (boundary < start)
                {
                    var count = Math.Min(32, tokens.Length - end);
                    boundary = Array.FindIndex(tokens, end, count, t => t == space || Tokenizer.PunctuationTokens.Contains(t));
                    end += count;
                }
                if (boundary >= start) end = boundary + 1;
            }
            yield return tokens[start..end];
            start = end;
        }
    }

    private byte[] Render(int[] tokens, KokoroVoice voice, float speed, CancellationToken token, Func<bool>? idle = null)
    {
        var key = new ClipKey(voice.Name, speed, Convert.ToBase64String(MemoryMarshal.AsBytes(tokens.AsSpan())));
        token.ThrowIfCancellationRequested();
        lock (_clips) if (_clips.TryGetValue(key, out var hit)) return hit;
        lock (_inference)
        {
            token.ThrowIfCancellationRequested();
            lock (_clips) if (_clips.TryGetValue(key, out var hit)) return hit;
            if (idle is not null && !idle()) return [];
            var samples = _model.Infer(tokens, voice.Features, speed);
            // A click arriving during speculative inference can use the clip that just finished.
            var pcm = ToPcm(samples, tokens);
            if (pcm.Length == 0 || pcm.Length > CacheBytes) return pcm;
            lock (_clips)
            {
                while (_cacheSize + pcm.Length > CacheBytes)
                {
                    var oldest = _arrival.Dequeue();
                    _cacheSize -= _clips[oldest].Length;
                    _clips.Remove(oldest);
                }
                _clips.Add(key, pcm);
                _arrival.Enqueue(key);
                _cacheSize += pcm.Length;
            }
            return pcm;
        }
    }

    private static byte[] ToPcm(float[] samples, int[] tokens)
    {
        // Trim padding before the first sound. Keep a short tail so consecutive words and phrases stay distinct.
        var first = Array.FindIndex(samples, sample => Math.Abs(sample) > .0001f);
        if (first < 0) return [];
        var last = Array.FindLastIndex(samples, sample => Math.Abs(sample) > .0001f);
        var ending = Array.FindLastIndex(tokens, t => t != Tokenizer.Vocab[' ']);
        var tail = ending < 0 ? 0 : Tokenizer.TokenToChar[tokens[ending]] switch
        {
            '.' or '?' or '!' or '\n' => 7200, ',' or ':' or ';' => 3000, _ => 1200
        };
        var end = Math.Min(samples.Length, last + 1 + tail);
        var pcm = new byte[(end - first) * sizeof(short)];
        var output = MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        for (var i = 0; i < output.Length; i++)
            output[i] = (short)(Math.Clamp(samples[first + i], -1, 1) * short.MaxValue);
        return pcm;
    }

    public void Dispose() => _model.Dispose();
}
