using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AiTranslate.Desktop.Controls;

/// <summary>A coral stroke moves at uniform speed along the composer's rounded outline.</summary>
internal sealed class FlowingBorderEffect : Control
{
    private const double Stroke = 1;
    private const double SecondsPerLap = 2.6;
    private const double TrailFraction = .28;
    private static readonly Pen Track = new(AppTheme.FlowTrack, Stroke);
    private readonly LinearGradientBrush _gradient = AppTheme.CreateFlowHighlight();
    private readonly Pen _trail;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
    private readonly Stopwatch _clock = new();
    private RectangleGeometry? _outline;
    private DashStyle? _dash;
    private double _lap;

    public FlowingBorderEffect()
    {
        IsHitTestVisible = false;
        IsVisible = false;
        _trail = new Pen(_gradient, Stroke, lineCap: PenLineCap.Round);
        _timer.Tick += (_, _) => InvalidateVisual();
        SizeChanged += (_, _) => ResizeOutline();
        AttachedToVisualTree += (_, _) => UpdateTimer();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) UpdateTimer();
        };
    }

    private void UpdateTimer()
    {
        if (IsVisible && TopLevel.GetTopLevel(this) is not null)
        {
            _clock.Restart();
            _timer.Start();
        }
        else _timer.Stop();
    }

    private void ResizeOutline()
    {
        var rect = new Rect(Bounds.Size).Deflate(Stroke / 2);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var radius = AppTheme.Radius.L - Stroke / 2;
        _outline = new RectangleGeometry(rect, radius, radius);
        // Dash lengths and offsets are expressed in multiples of the pen width.
        _lap = _outline.ContourLength;
        _trail.DashStyle = _dash = new DashStyle([_lap * TrailFraction / Stroke, _lap * (1 - TrailFraction) / Stroke], 0);
    }

    public override void Render(DrawingContext context)
    {
        if (_outline is null || _dash is null || _lap <= 0) return;
        var tail = _clock.Elapsed.TotalSeconds / SecondsPerLap % 1 * _lap;
        var head = (tail + _lap * TrailFraction) % _lap;
        if (_outline.TryGetPointAtDistance(tail, out var from) && _outline.TryGetPointAtDistance(head, out var to))
        {
            _gradient.StartPoint = new RelativePoint(from, RelativeUnit.Absolute);
            _gradient.EndPoint = new RelativePoint(to, RelativeUnit.Absolute);
        }
        _dash.Offset = -tail / Stroke;
        context.DrawGeometry(null, Track, _outline);
        context.DrawGeometry(null, _trail, _outline);
    }
}
