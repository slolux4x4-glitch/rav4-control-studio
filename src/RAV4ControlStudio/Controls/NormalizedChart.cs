using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RAV4ControlStudio.ViewModels;

namespace RAV4ControlStudio.Controls;

/// <summary>
/// Multi-line graph where each line is scaled to its own Min/Max (shown as 0 to 100 %), so parameters with
/// different units can be compared by shape. Uses the same fixed 3-minute window as the Overview graph.
/// </summary>
public sealed class NormalizedChart : FrameworkElement
{
    private const int Divisions = 4;
    private const double WindowSeconds = 180;

    private Brush? _muted;
    private Pen? _gridPen;
    private Pen? _axisPen;
    private readonly Dictionary<Brush, Pen> _pens = new();
    private readonly TextCache _labels = new();

    public Func<IEnumerable<ChartSeries>>? Provider { get; set; }

    public IReadOnlyList<double>? Times { get; set; }

    public void Redraw() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth, h = ActualHeight;
        if (w < 200 || h < 100) return;

        _muted ??= (Brush)FindResource("MutedBrush");
        _gridPen ??= CreatePen();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double left = 52, top = 10, right = w - 14, bottom = h - 26;
        for (int i = 0; i <= Divisions; i++)
        {
            double y = bottom - (bottom - top) * i / Divisions;
            dc.DrawLine(_gridPen, new Point(left, y), new Point(right, y));
            Label(dc, $"{i * 100 / Divisions}%", _muted, dpi, left - 8, y, TextAlignment.Right);
        }
        var axisPen = PenCache.Get(ref _axisPen, _muted, 1);
        dc.DrawLine(axisPen, new Point(left, top), new Point(left, bottom));
        dc.DrawLine(axisPen, new Point(left, bottom), new Point(right, bottom));

        var times = Times;
        double now = times is { Count: > 0 } ? times[^1] : 0;
        double t0 = Math.Max(0, now - WindowSeconds);
        double X(double t) => left + (t - t0) / WindowSeconds * (right - left);

        for (int k = 0; k <= 6; k++)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, t0 + k * WindowSeconds / 6));
            Label(dc, $"{(int)span.TotalMinutes:00}:{span.Seconds:00}", _muted, dpi, left + (right - left) * k / 6, bottom + 14, TextAlignment.Center);
        }

        if (times is null || times.Count < 2 || Provider is null) return;

        foreach (var s in Provider())
        {
            if (s.Values.Count < 2) continue;

            if (!_pens.TryGetValue(s.Brush, out var pen))
            {
                pen = new Pen(s.Brush, 2) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                _pens[s.Brush] = pen;
            }
            double range = s.Max - s.Min;
            int offset = times.Count - s.Values.Count;

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
                    var p = new Point(X(times[ti]), bottom - norm * (bottom - top));
                    if (!drawing) { ctx.BeginFigure(p, false, false); drawing = true; }
                    else ctx.LineTo(p, true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    private static Pen CreatePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x2E, 0x3D)), 1) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }

    private void Label(DrawingContext dc, string text, Brush brush, double dpi, double x, double y, TextAlignment alignment)
    {
        var f = _labels.Get(text, 12, brush, dpi);
        double px = alignment switch
        {
            TextAlignment.Right => x - f.Width,
            TextAlignment.Center => x - f.Width / 2,
            _ => x,
        };
        dc.DrawText(f, new Point(px, y - f.Height / 2));
    }
}
