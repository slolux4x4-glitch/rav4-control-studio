using System.Windows;
using System.Windows.Media;
using RAV4ControlStudio.ViewModels;

namespace RAV4ControlStudio.Controls;

/// <summary>
/// Shared drawing for the live graphs: a fixed 3-minute time window that scrolls once the session is longer, time
/// labels every 30 s, and one line per series with gaps where samples are missing (NaN).
/// The grid and axis labels only change when the size or units change, so they are drawn once and reused;
/// each refresh only draws the time labels and the lines.
/// </summary>
public abstract class ChartBase : FrameworkElement
{
    protected const double WindowSeconds = 180;
    private const int TimeDivisions = 6;

    private readonly TextCache _labels = new();
    private readonly Dictionary<Brush, Pen> _thinPens = new();
    private readonly Dictionary<Brush, Pen> _linePens = new();
    private Brush? _muted;
    private Pen? _gridPen;

    private DrawingGroup? _staticLayer;
    private (double W, double H, double Dpi, int Version) _staticKey;

    /// <summary>Seconds since connect, one entry per sample.</summary>
    public IReadOnlyList<double>? Times { get; set; }

    public void Redraw() => InvalidateVisual();

    protected Brush Muted => _muted ??= (Brush)FindResource("MutedBrush");

    protected Pen GridPen => _gridPen ??= CreateGridPen();

    /// <summary>Smallest size worth drawing.</summary>
    protected virtual Size MinimumChartSize => new(200, 100);

    /// <summary>The plot rectangle inside the control (axes and labels sit outside it).</summary>
    protected abstract Rect PlotArea(double width, double height);

    /// <summary>Draws the grid, axis labels and axis lines. Called only when <see cref="StaticVersion"/> or the size changes.</summary>
    protected abstract void DrawStatic(DrawingContext dc, Rect plot, double dpi);

    /// <summary>Changes whenever the static layer must be redrawn (for example when units change).</summary>
    protected virtual int StaticVersion => 0;

    /// <summary>Series to draw as lines this frame.</summary>
    protected abstract IEnumerable<ChartSeries> SeriesToDraw();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth, h = ActualHeight;
        if (w < MinimumChartSize.Width || h < MinimumChartSize.Height) return;

        var plot = PlotArea(w, h);
        if (plot.Width < 40 || plot.Height < 20) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var key = (w, h, dpi, StaticVersion);
        if (_staticLayer is null || key != _staticKey)
        {
            var layer = new DrawingGroup();
            using (var ctx = layer.Open())
                DrawStatic(ctx, plot, dpi);
            layer.Freeze();
            _staticLayer = layer;
            _staticKey = key;
        }
        dc.DrawDrawing(_staticLayer);

        var times = Times;
        double now = times is { Count: > 0 } ? times[^1] : 0;
        double t0 = Math.Max(0, now - WindowSeconds);

        for (int k = 0; k <= TimeDivisions; k++)
        {
            var span = TimeSpan.FromSeconds(t0 + k * WindowSeconds / TimeDivisions);
            Label(dc, $"{(int)span.TotalMinutes:00}:{span.Seconds:00}", 12, Muted, dpi,
                plot.Left + plot.Width * k / TimeDivisions, plot.Bottom + 14, TextAlignment.Center);
        }

        if (times is null || times.Count < 2) return;

        foreach (var s in SeriesToDraw())
            DrawSeries(dc, s, times, t0, plot);
    }

    private void DrawSeries(DrawingContext dc, ChartSeries s, IReadOnlyList<double> times, double t0, Rect plot)
    {
        if (s.Values.Count < 2) return;

        double range = s.Max - s.Min;
        int offset = times.Count - s.Values.Count;
        double X(double t) => plot.Left + (t - t0) / WindowSeconds * plot.Width;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            bool drawing = false;
            for (int i = 0; i < s.Values.Count; i++)
            {
                int ti = i + offset;
                double v = s.Values[i];
                if (ti < 0 || ti >= times.Count || times[ti] < t0 || double.IsNaN(v)) { drawing = false; continue; }

                double norm = range <= 0 ? 0 : Math.Clamp((v - s.Min) / range, 0, 1);
                var p = new Point(X(times[ti]), plot.Bottom - norm * plot.Height);
                if (!drawing) { ctx.BeginFigure(p, false, false); drawing = true; }
                else ctx.LineTo(p, true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, PenFor(_linePens, s.Brush, 2), geometry);
    }

    /// <summary>A frozen 1-pixel pen in the given colour, reused between frames.</summary>
    protected Pen ThinPen(Brush brush) => PenFor(_thinPens, brush, 1);

    private static Pen PenFor(Dictionary<Brush, Pen> cache, Brush brush, double thickness)
    {
        if (cache.TryGetValue(brush, out var pen)) return pen;
        pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return cache[brush] = pen;
    }

    protected void Label(DrawingContext dc, string text, double size, Brush brush, double dpi,
        double x, double y, TextAlignment alignment)
    {
        var f = _labels.Get(text, size, brush, dpi);
        double px = alignment switch
        {
            TextAlignment.Right => x - f.Width,
            TextAlignment.Center => x - f.Width / 2,
            _ => x,
        };
        dc.DrawText(f, new Point(px, y - f.Height / 2));
    }

    private static Pen CreateGridPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x2E, 0x3D)), 1) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }
}
