using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using System.Threading.Channels;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using KokoroSharp.Processing;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class SpeechStreamingTests
{
    [Fact]
    public void AudioStreamSurvivesUnderrunsAndSignalsPlaybackOnlyOnTheFirstActualSamples()
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        var starts = 0;
        var source = new SpeechPcmOutput.PcmProvider(channel.Reader, () => starts++);
        var buffer = new byte[4];
        Assert.Equal(4, source.Read(buffer, 0, 4));
        Assert.Equal(0, starts);
        channel.Writer.TryWrite([1, 2]);
        Assert.Equal(4, source.Read(buffer, 0, 4));
        Assert.Equal(new byte[] { 1, 2, 0, 0 }, buffer);
        channel.Writer.TryWrite([3, 4]);
        channel.Writer.TryWrite([5, 6, 7, 8]);
        channel.Writer.Complete();
        Assert.Equal(4, source.Read(buffer, 0, 4));
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, buffer);
        Assert.Equal(2, source.Read(buffer, 0, 4));
        Assert.Equal(new byte[] { 7, 8 }, buffer[..2]);
        Assert.Equal(0, source.Read(buffer, 0, 4));
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData("ə lɒŋ laɪn, wɪð sɛvɹəl fɹeɪzɪz.\n")]
    [InlineData("ɑbədi")]
    public void ShortOpeningChunkAndSubsequentChunksPreserveEveryToken(string phrase)
    {
        var tokens = string.Concat(Enumerable.Repeat(phrase, 128)).Select(c => Tokenizer.Vocab[c]).ToArray();
        var segments = KokoroAudio.Segments(tokens).ToArray();
        Assert.True(segments.Length > 1);
        Assert.InRange(segments[0].Length, 1, 56);
        Assert.All(segments, s => Assert.InRange(s.Length, 1, 112));
        Assert.Equal(tokens, segments.SelectMany(s => s));
    }

    [Fact]
    public async Task ButtonShowsPreparationImmediatelyAndDoesNotRestartPendingSpeech()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        await session.Dispatch(() =>
        {
            var playback = new SpeechPlayback();
            var requests = 0;
            var button = SpeechPlaybackButton.Create(click => StudioUi.CreateCompactButton("volume-high", "跟读", click), p =>
            {
                requests++;
                p.SetState(SpeechPlaybackState.Preparing);
                return Task.CompletedTask;
            }, playback);
            var window = new Window { Content = button };
            window.Show();
            try
            {
                void Click() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Click();
                Assert.Equal("正在准备语音", AutomationProperties.GetItemStatus(button));
                Click();
                Assert.Equal(1, requests);
                window.Content = null;
                playback.SetState(SpeechPlaybackState.Playing);
                window.Content = button;
                Assert.Equal("正在播放", AutomationProperties.GetItemStatus(button));
                playback.SetState(SpeechPlaybackState.Idle);
                Assert.False(button.Classes.Contains("playing"));
                Click();
                Assert.Equal(2, requests);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
