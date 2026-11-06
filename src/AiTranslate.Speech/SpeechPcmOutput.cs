using System.Threading.Channels;
using KokoroSharp.Utilities;
using NAudio.Wave;

namespace AiTranslate.Speech;

internal static class SpeechPcmOutput
{
    private static readonly WaveFormat Format = new(24000, 16, 1);

    public static async Task PlayAsync(ChannelReader<byte[]> chunks, Action started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            // Keep the same output stream open across inference chunks, including temporary underruns.
            using var output = new WaveOutEvent { DesiredLatency = 80 };
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is { } error) stopped.TrySetException(error);
                else stopped.TrySetResult();
            };
            output.Init(new PcmProvider(chunks, started));
            token.ThrowIfCancellationRequested();
            output.Play();
            await stopped.Task.WaitAsync(token).ConfigureAwait(false);
            if (!chunks.Completion.IsCompleted) throw new IOException("The audio device stopped before the end of the stream.");
            await chunks.Completion.ConfigureAwait(false);
            return;
        }

        var first = true;
        await foreach (var pcm in chunks.ReadAllAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            using var stream = new RawSourceWaveStream(pcm, 0, pcm.Length, Format);
            var player = CrossPlatformHelper.GetAudioPlayer();
            try
            {
                player.Init(stream);
                player.Play();
                if (first) { started(); first = false; }
                while (player.PlaybackState == PlaybackState.Playing)
                    await Task.Delay(10, token).ConfigureAwait(false);
            }
            finally { player.Dispose(); }
        }
    }

    internal sealed class PcmProvider(ChannelReader<byte[]> chunks, Action started) : IWaveProvider
    {
        public WaveFormat WaveFormat => Format;
        private byte[]? _current;
        private int _offset;
        private bool _started;

        public int Read(byte[] buffer, int offset, int count)
        {
            var copied = 0;
            while (copied < count)
            {
                if (_current is null && !chunks.TryRead(out _current))
                {
                    if (chunks.Completion.IsCompleted) break;
                    Array.Clear(buffer, offset + copied, count - copied);
                    return count;
                }
                var take = Math.Min(_current.Length - _offset, count - copied);
                Buffer.BlockCopy(_current, _offset, buffer, offset + copied, take);
                if (!_started)
                {
                    _started = true;
                    started();
                }
                copied += take;
                _offset += take;
                if (_offset == _current.Length) { _current = null; _offset = 0; }
            }
            return copied;
        }
    }
}
