using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RAV4ControlStudio.ViewModels;

namespace RAV4ControlStudio.Controls;

/// <summary>
/// Live graph with the concept's three scales: left axis for series 0 (rpm), right inner axis for
/// series 2 (voltage) and right outer axis for series 1 (coolant). The x axis is a fixed 3-minute
/// window (00:00 to 03:00) that scrolls once the session is longer than that. NaN samples leave gaps.
/// </summary>
public sealed class TimeSeriesChart : FrameworkElement
{
    private const int Divisions = 6;
    private const double WindowSeconds = 180;

    private Brush? _muted;
    private Pen? _gridPen;
    private readonly Dictionary<ChartSeries, Pen> _pens = new();
    private readonly Dictionary<Brush, Pen> _rulePens = new();
    private readonly TextCache _labels = new();

    private Pen RulePen(Brush brush)
    {
        if (_rulePens.TryGetValue(brush, out var pen)) return pen;
        pen = new Pen(brush, 1);
        pen.Freeze();
        return _rulePens[brush] = pen;
    }

    private static Pen CreateGridPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x2E, 0x3D)), 1) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }

    private Pen PenFor(ChartSeries s)
    {
        if (_pens.TryGetValue(s, out var pen)) return pen;
        pen = new Pen(s.Brush, 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return _pens[s] = pen;
    }

    public IReadOnlyList<ChartSeries>? Series { get; set; }

    /// <summary>Seconds since connect, one entry per sample (same length as the series values).</summary>
    public IReadOnlyList<double>? Times { get; set; }

    public void Redraw() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth, h = ActualHeight;
        if (w < 300 || h < 100) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _muted ??= (Brush)FindResource("MutedBrush");
        var muted = _muted;
        var gridPen = _gridPen ??= CreateGridPen();

        double left = 56, top = 10, bottom = h - 26, right = w - 110;
        if (right <= left + 40) return;

        var series = Series ?? Array.Empty<ChartSeries>();

        for (int i = 0; i <= Divisions; i++)
        {
            double y = bottom - (bottom - top) * i / Divisions;
            dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));

            if (series.Count > 0)
                Label(dc, FormatAxis(series[0], i), 12, muted, dpi, left - 8, y, TextAlignment.Right);
            if (series.Count > 2)
                Label(dc, FormatAxis(series[2], i), 12, series[2].Brush, dpi, right + 10, y, TextAlignment.Left);
            if (series.Count > 1)
                Label(dc, FormatAxis(series[1], i, true), 12, series[1].Brush, dpi, right + 62, y, TextAlignment.Left);
        }

        // Axis rules like the concept: a coloured line beside each right-hand scale
        if (series.Count > 2)
            dc.DrawLine(RulePen(series[2].Brush), new Point(right + 2, top), new Point(right + 2, bottom));
        if (series.Count > 1)
            dc.DrawLine(RulePen(series[1].Brush), new Point(right + 54, top), new Point(right + 54, bottom));
        dc.DrawLine(RulePen(muted), new Point(left, top), new Point(left, bottom));
        dc.DrawLine(RulePen(muted), new Point(left, bottom), new Point(right, bottom));

        var times = Times;
        double now = times is { Count: > 0 } ? times[^1] : 0;
        double t0 = Math.Max(0, now - WindowSeconds);
        double X(double t) => left + (t - t0) / WindowSeconds * (right - left);

        // Time labels every 30 s
        for (int k = 0; k <= Divisions; k++)
            Label(dc, FormatTime(t0 + k * WindowSeconds / Divisions), 12, muted, dpi,
                left + (right - left) * k / Divisions, bottom + 14, TextAlignment.Center);

        if (times is null || times.Count < 2) return;

        foreach (var s in series)
        {
            if (!s.IsVisible || s.Values.Count < 2) continue;

            var pen = PenFor(s);
            double range = s.Max - s.Min;
            int offset = times.Count - s.Values.Count;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                bool drawing = false;
                for (int i = 0; i < s.Values.Count; i++)
                {
                    int ti = i + offset;
                    if (ti < 0 || ti >= times.Count || times[ti] < t0) { drawing = false; continue; }

                    double v = s.Values[i];
                    if (double.IsNaN(v)) { drawing = false; continue; }

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

    private static string FormatAxis(ChartSeries s, int step, bool temperature = false)
    {
        double value = s.Min + (s.Max - s.Min) * step / Divisions;
        if (temperature && Units.Imperial) value = value * 9 / 5 + 32;
        return s.Max - s.Min >= 20
            ? value.ToString("#,##0", CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }

    private void Label(DrawingContext dc, string text, double size, Brush brush, double dpi,
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
}
