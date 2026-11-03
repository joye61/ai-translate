using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace AiTranslate.Desktop.Controls;

/// <summary>A ruled sheet with a pencil tracing a line of shorthand while the entry is being written.</summary>
internal sealed class DraftPaperLoading : Border
{
    private readonly TextBlock _caption = TextStyles.Create("正在起草译文…", TextRole.FieldLabel);

    public DraftPaperLoading()
    {
        Background = AppTheme.InputBg;
        CornerRadius = new CornerRadius(AppTheme.Radius.M);
        ClipToBounds = true;
        IsHitTestVisible = false;
        _caption.Margin = new Thickness(32, 9, 12, 0);
        _caption.VerticalAlignment = VerticalAlignment.Top;
        Child = new Panel { Children = { new WritingLines(), _caption } };
    }

    public void ShowDetailCaption(bool passage) => _caption.Text = passage ? "正在整理译注…" : "正在整理释义…";

    private sealed class WritingLines : Decorator
    {
        private static readonly Pen Rule = new(AppTheme.BorderStrong, .7);
        private static readonly Pen MarginRule = new(AppTheme.FlowTrack, 1);
        private static readonly Pen Ink = new(AppTheme.TextMuted, 1.45, lineCap: PenLineCap.Round);
        // Each curve travels left to right, so clipping it at the nib reveals only the ink already laid down.
        private static readonly Geometry Handwriting = StreamGeometry.Parse(
            "M0,0 Q3,-1 6,-5 Q10,-12 9,-7 Q6,3 11,-1 Q15,-6 15,-3 Q14,1 17,-1 Q21,-5 23,-2 Q25,0 30,-2 " +
            "Q36,-4 39,-6 Q47,-16 46,-6 Q43,2 48,-2 Q53,-6 52,-3 Q51,1 56,-2 Q60,-5 62,-2 Q64,0 70,-3 " +
            "Q81,-5 84,-8 Q88,-15 87,-6 Q84,2 90,-2 Q94,-5 96,-2 Q98,0 102,-3 Q105,-5 108,-3 Q111,0 119,-3");
        private const double Left = 32, Baseline = 54, Pitch = 22;
        private readonly TranslateTransform _position = new();
        private readonly MdiIcon _pencil = new("pencil-outline", 19, AppTheme.AccentBright);
        private static readonly StyledProperty<double> ProgressProperty =
            AvaloniaProperty.Register<WritingLines, double>("Progress");
        private int _row;
        private Point _nib;
        private double _fade = 1;

        public WritingLines()
        {
            _pencil.HorizontalAlignment = HorizontalAlignment.Left;
            _pencil.VerticalAlignment = VerticalAlignment.Top;
            _pencil.RenderTransform = _position;
            Child = _pencil;
            Styles.Add(new Style(x => x.OfType<WritingLines>())
            {
                Animations =
                {
                    new Animation
                    {
                        Duration = TimeSpan.FromSeconds(5.2), IterationCount = IterationCount.Infinite,
                        Children =
                        {
                            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ProgressProperty, 0d) } },
                            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ProgressProperty, 1d) } }
                        }
                    }
                }
            });
            SizeChanged += (_, _) => UpdateFrame();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ProgressProperty) UpdateFrame();
        }

        private double Scale(int row) => Math.Clamp((Bounds.Width - Left - 34) / 119, 0, 1.4) * (1 - .13 * row);

        private void UpdateFrame()
        {
            var rows = Math.Clamp((int)((Bounds.Height - Baseline) / Pitch) + 1, 1, 3);
            var phase = GetValue(ProgressProperty) * (rows * 1.55 + .55);
            _row = Math.Min((int)(phase / 1.55), rows - 1);
            var progress = Math.Clamp((phase - _row * 1.55) / 1.35, 0, 1);
            Handwriting.TryGetPointAtDistance(Handwriting.ContourLength * progress, out _nib);
            _fade = Math.Clamp((rows * 1.55 + .55 - phase) / .45, 0, 1);
            _position.X = Left + _nib.X * Scale(_row) - 2.4;
            _position.Y = Baseline + Pitch * _row - 2 + _nib.Y - 16.6;
            _pencil.Opacity = _fade;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            var right = Bounds.Width - AppTheme.Space.L;
            if (right <= Left || Bounds.Height <= 0) return;
            context.DrawLine(MarginRule, new Point(21, 0), new Point(21, Bounds.Height));
            for (double y = Baseline; y < Bounds.Height; y += Pitch)
                context.DrawLine(Rule, new Point(8, y), new Point(right, y));
            using var opacity = context.PushOpacity(_fade);
            for (var row = 0; row <= _row; row++)
            {
                using (context.PushTransform(Matrix.CreateScale(Scale(row), 1) * Matrix.CreateTranslation(Left, Baseline + Pitch * row - 2)))
                using (context.PushClip(new Rect(-1, -16, row == _row ? Math.Max(0, _nib.X) + 2 : 122, 20)))
                    context.DrawGeometry(null, Ink, Handwriting);
            }
        }
    }
}
