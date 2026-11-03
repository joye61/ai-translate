using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;

namespace AiTranslate.Desktop;

/// <summary>A separate visual layer: animation never changes captured or exported pixels. Ported from XCapture.</summary>
internal sealed class OcrScanEffect : Control
{
    private const double SweepSeconds = 2.2;
    private CompositionCustomVisual? _visual;
    private static readonly IImmutableBrush Trail = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, .5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, .5, RelativeUnit.Relative),
        GradientStops =
        [
            new(Color.FromArgb(0, 255, 122, 89), 0),
            new(Color.FromArgb(2, 255, 122, 89), .2),
            new(Color.FromArgb(8, 255, 122, 89), .45),
            new(Color.FromArgb(20, 255, 130, 98), .7),
            new(Color.FromArgb(38, 255, 144, 114), .9),
            new(Color.FromArgb(48, 255, 160, 132), 1)
        ]
    }.ToImmutable();
    private static readonly IImmutableBrush Glow = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, .5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, .5, RelativeUnit.Relative),
        GradientStops =
        [
            new(Color.FromArgb(0, 255, 160, 132), 0),
            new(Color.FromArgb(6, 255, 160, 132), .2),
            new(Color.FromArgb(26, 255, 178, 152), .4),
            new(Color.FromArgb(55, 255, 200, 178), .5),
            new(Color.FromArgb(26, 255, 178, 152), .6),
            new(Color.FromArgb(6, 255, 160, 132), .8),
            new(Color.FromArgb(0, 255, 160, 132), 1)
        ]
    }.ToImmutable();
    private static readonly IImmutableBrush Beam = new SolidColorBrush(Color.FromArgb(150, 255, 206, 188)).ToImmutable();
    private static readonly IImmutableBrush EdgeFeather = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(.5, 1, RelativeUnit.Relative),
        GradientStops =
        [
            new(Colors.Transparent, 0), new(Color.FromArgb(150, 255, 255, 255), .08),
            new(Colors.White, .22), new(Colors.White, .78),
            new(Color.FromArgb(150, 255, 255, 255), .92), new(Colors.Transparent, 1)
        ]
    }.ToImmutable();
    private long _started;
    public bool IsScanning { get; private set; }

    public OcrScanEffect()
    {
        IsHitTestVisible = false; ClipToBounds = true; IsVisible = false;
    }

    public void Start(long started)
    {
        if (IsScanning && _started == started) return;
        _started = started; IsScanning = IsVisible = true;
        SendState();
    }

    public void Stop()
    {
        IsScanning = IsVisible = false;
        SendState();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null) return;
        _visual = compositor.CreateCustomVisual(new ScanVisualHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        UpdateSize();
        SendState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _visual?.SendHandlerMessage(new ScanState(false, _started));
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) UpdateSize();
    }

    private void UpdateSize()
    {
        if (_visual != null) _visual.Size = new Vector(Bounds.Width, Bounds.Height);
    }

    private void SendState() => _visual?.SendHandlerMessage(new ScanState(IsScanning, _started));

    private readonly record struct ScanState(bool Running, long Started);

    private sealed class ScanVisualHandler : CompositionCustomVisualHandler
    {
        private bool _running, _framePending;
        private long _started;
        private double _head, _trailWidth, _opacity;
        private Rect _bandBounds;

        public override void OnMessage(object message)
        {
            if (message is not ScanState state) return;
            InvalidateBand();
            _running = state.Running;
            _started = state.Started;
            if (!_running) { _bandBounds = default; return; }
            UpdateFrame();
            InvalidateBand();
            RequestFrame();
        }

        public override void OnAnimationFrameUpdate()
        {
            _framePending = false;
            if (!_running) return;
            InvalidateBand();
            UpdateFrame();
            InvalidateBand();
            RequestFrame();
        }

        private void RequestFrame()
        {
            if (_framePending) return;
            _framePending = true;
            RegisterForNextAnimationFrameUpdate();
        }

        private void UpdateFrame()
        {
            var phase = Stopwatch.GetElapsedTime(_started).TotalSeconds % SweepSeconds / SweepSeconds;
            _trailWidth = Math.Clamp(EffectiveSize.X * .2, 40, 180);
            _head = -10 + phase * (EffectiveSize.X + _trailWidth + 20);
            var fade = Math.Clamp(Math.Min(phase / .08, (1 - phase) / .12), 0, 1);
            _opacity = fade * fade * (3 - 2 * fade);
            _bandBounds = new Rect(_head - _trailWidth, 0, _trailWidth + 8, EffectiveSize.Y);
        }

        private void InvalidateBand()
        {
            var visible = _bandBounds.Inflate(1).Intersect(new Rect(0, 0, EffectiveSize.X, EffectiveSize.Y));
            if (visible.Width > 0 && visible.Height > 0) Invalidate(visible);
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            if (!_running || EffectiveSize.X <= 0 || EffectiveSize.Y <= 0) return;
            using (context.PushClip(new Rect(0, 0, EffectiveSize.X, EffectiveSize.Y)))
            using (context.PushOpacity(_opacity, _bandBounds))
            using (context.PushOpacityMask(EdgeFeather, _bandBounds))
            {
                context.DrawRectangle(Trail, null, new Rect(_head - _trailWidth, 0, _trailWidth, EffectiveSize.Y));
                context.DrawRectangle(Glow, null, new Rect(_head - 8, 0, 16, EffectiveSize.Y));
                context.DrawRectangle(Beam, null, new Rect(_head - .6, 0, 1.2, EffectiveSize.Y));
            }
        }
    }
}
