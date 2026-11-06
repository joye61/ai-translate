using AiTranslate.Speech;
using Xunit;

namespace AiTranslate.Tests;

public sealed class SpeechPreloadTests
{
    [Fact]
    public async Task EarlyPlaybackSharesPreloadAndCancelingPlaybackOnlyCancelsItsWait()
    {
        using var speech = new KokoroSpeechService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        speech.StatusChanged += (_, status) =>
        {
            if (!status.StartsWith("正在后台加载", StringComparison.Ordinal)) return;
            Interlocked.Increment(ref attempts);
            started.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
        };

        var preload = speech.EnsureKokoroModelReadyAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(preload.IsCompleted);
            Assert.Same(preload, speech.EnsureKokoroModelReadyAsync());
            using var cancel = new CancellationTokenSource();
            var playback = new SpeechPlayback();
            var firstReading = speech.SpeakAsync("test", cancellationToken: cancel.Token, playback: playback);
            Assert.Equal(SpeechPlaybackState.Preparing, playback.State);
            cancel.Cancel();
            await firstReading.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(SpeechPlaybackState.Idle, playback.State);
            Assert.False(preload.IsCompleted);
        }
        finally { release.Set(); }

        Assert.False(await preload);
        Assert.Contains("系统语音", speech.EngineStatus);
        Assert.Equal(1, attempts);
        Assert.False(await speech.EnsureKokoroModelReadyAsync());
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ReplacingACardReadingDuringPreloadCancelsItsRemainingParts()
    {
        using var speech = new KokoroSpeechService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var release = new ManualResetEventSlim();
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        speech.StatusChanged += (_, status) =>
        {
            if (!status.StartsWith("正在后台加载", StringComparison.Ordinal)) return;
            loading.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
        };

        var card = new SpeechPlayback();
        var preload = speech.EnsureKokoroModelReadyAsync();
        var sequence = speech.SpeakVocabularyCardAsync("first", null, null, "An example.", playback: card);
        try
        {
            await loading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = speech.SpeakAsync("second");
            await sequence.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(card.IsPlaying);
            Assert.False(replacement.IsCompleted);
            speech.Stop();
            await replacement.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            speech.Stop();
            release.Set();
            await preload;
        }
    }

    [Fact]
    public async Task ExitingDuringPreloadDoesNotWaitForModelLoading()
    {
        using var speech = new KokoroSpeechService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        speech.StatusChanged += (_, _) =>
        {
            started.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
        };
        var preload = speech.EnsureKokoroModelReadyAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            speech.Dispose();
            Assert.False(preload.IsCompleted);
            Assert.False(await speech.EnsureKokoroModelReadyAsync());
        }
        finally { release.Set(); }
        Assert.False(await preload);
    }
}
