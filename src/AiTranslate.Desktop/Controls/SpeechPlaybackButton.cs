using AiTranslate.Speech;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiTranslate.Desktop.Controls;

internal static class SpeechPlaybackButton
{
    private static readonly string[] Waves = ["volume-low", "volume-medium", "volume-high"];

    public static Button Create(Func<Action, Button> build, Func<SpeechPlayback, Task> speak, SpeechPlayback? playback = null)
    {
        playback ??= new SpeechPlayback();
        var button = build(() => _ = speak(playback));
        var icon = button.Content as MdiIcon ?? ((Panel)button.Content!).Children.OfType<MdiIcon>().Single();
        var restIcon = icon.IconName;
        var frame = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(190) };
        timer.Tick += (_, _) => icon.IconName = Waves[++frame % Waves.Length];
        button.Classes.Add("speech");
        icon.ClearValue(MdiIcon.ForegroundProperty);

        void SetPlaying(bool playing)
        {
            if (button.Classes.Contains("playing") == playing) return;
            button.Classes.Set("playing", playing);
            AutomationProperties.SetItemStatus(button, playing ? "正在播放" : "");
            if (playing)
            {
                frame = 0;
                icon.IconName = Waves[frame];
                timer.Start();
            }
            else
            {
                timer.Stop();
                icon.IconName = restIcon;
            }
        }

        void Refresh() => SetPlaying(playback.IsPlaying && button.IsAttachedToVisualTree());
        void Changed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);
        button.AttachedToVisualTree += (_, _) =>
        {
            playback.Changed += Changed;
            Refresh();
        };
        button.DetachedFromVisualTree += (_, _) =>
        {
            playback.Changed -= Changed;
            SetPlaying(false);
        };
        return button;
    }
}
