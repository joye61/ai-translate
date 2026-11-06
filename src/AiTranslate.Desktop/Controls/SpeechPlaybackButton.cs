using AiTranslate.Speech;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiTranslate.Desktop.Controls;

internal static class SpeechPlaybackButton
{
    private static readonly string[] Waves = ["volume-off", "volume-low", "volume-high"];

    public static Button Create(Func<Action, Button> build, Func<SpeechPlayback, Task> speak, SpeechPlayback? playback = null)
    {
        playback ??= new SpeechPlayback();
        var button = build(() =>
        {
            if (playback.State != SpeechPlaybackState.Preparing) _ = speak(playback);
        });
        var icon = button.Content as FontAwesomeIcon ?? ((Panel)button.Content!).Children.OfType<FontAwesomeIcon>().Single();
        var restIcon = icon.IconName;
        var frame = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(190) };
        timer.Tick += (_, _) => icon.IconName = Waves[++frame % Waves.Length];
        button.Classes.Add("speech");
        icon.ClearValue(FontAwesomeIcon.ForegroundProperty);

        var displayed = SpeechPlaybackState.Idle;
        void ShowState(SpeechPlaybackState state)
        {
            if (displayed == state) return;
            displayed = state;
            button.Classes.Set("preparing", state == SpeechPlaybackState.Preparing);
            button.Classes.Set("playing", state == SpeechPlaybackState.Playing);
            AutomationProperties.SetItemStatus(button, state switch
                { SpeechPlaybackState.Preparing => "正在准备语音", SpeechPlaybackState.Playing => "正在播放", _ => "" });
            if (state == SpeechPlaybackState.Playing)
            {
                frame = 0;
                icon.IconName = Waves[frame];
                timer.Start();
            }
            else
            {
                timer.Stop();
                icon.IconName = state == SpeechPlaybackState.Preparing ? "hourglass-half" : restIcon;
            }
        }

        void Refresh() => ShowState(button.IsAttachedToVisualTree() ? playback.State : SpeechPlaybackState.Idle);
        void Changed(object? sender, EventArgs e)
        {
            if (Dispatcher.UIThread.CheckAccess()) Refresh();
            else Dispatcher.UIThread.Post(Refresh);
        }
        button.AttachedToVisualTree += (_, _) =>
        {
            playback.Changed += Changed;
            Refresh();
        };
        button.DetachedFromVisualTree += (_, _) =>
        {
            playback.Changed -= Changed;
            ShowState(SpeechPlaybackState.Idle);
        };
        return button;
    }
}
