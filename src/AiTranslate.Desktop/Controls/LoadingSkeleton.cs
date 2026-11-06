using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;

namespace AiTranslate.Desktop.Controls;

/// <summary>Shared breathing placeholder for dictionary content that is still arriving.</summary>
internal sealed class LoadingSkeleton : StackPanel
{
    private CancellationTokenSource? _animation;

    public LoadingSkeleton()
    {
        Spacing = AppTheme.Space.M;
        foreach (var (width, height) in new[] { (96d, 14d), (320d, 12d), (260d, 12d), (200d, 12d) })
            Children.Add(new Border
            {
                Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Background = AppTheme.CardElevatedBg
            });
        AttachedToVisualTree += (_, _) =>
        {
            _animation = new CancellationTokenSource();
            _ = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(900), IterationCount = IterationCount.Infinite,
                PlaybackDirection = PlaybackDirection.Alternate, Easing = new Avalonia.Animation.Easings.SineEaseInOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1d) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.45d) } }
                }
            }.RunAsync(this, _animation.Token);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _animation?.Cancel();
            _animation?.Dispose();
            _animation = null;
        };
    }
}
