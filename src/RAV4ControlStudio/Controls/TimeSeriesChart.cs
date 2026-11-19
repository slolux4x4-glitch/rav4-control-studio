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
public sealed class TimeSeriesChart : ChartBase
{
    private const int Divisions = 6;

    public IReadOnlyList<ChartSeries>? Series { get; set; }

    protected override Size MinimumChartSize => new(300, 100);

    protected override Rect PlotArea(double width, double height) =>
        new(new Point(56, 10), new Point(Math.Max(56, width - 110), height - 26));

    // The coolant scale switches between °C and °F.
    protected override int StaticVersion => Units.Imperial ? 1 : 0;

    protected override IEnumerable<ChartSeries> SeriesToDraw() =>
        (Series ?? Array.Empty<ChartSeries>()).Where(s => s.IsVisible);

    protected override void DrawStatic(DrawingContext dc, Rect plot, double dpi)
    {
        var series = Series ?? Array.Empty<ChartSeries>();
        double left = plot.Left, right = plot.Right, top = plot.Top, bottom = plot.Bottom;

        for (int i = 0; i <= Divisions; i++)
        {
            double y = bottom - (bottom - top) * i / Divisions;
            dc.DrawLine(GridPen, new Point(left, y), new Point(right, y));

            if (series.Count > 0)
                Label(dc, FormatAxis(series[0], i), 12, Muted, dpi, left - 8, y, TextAlignment.Right);
            if (series.Count > 2)
                Label(dc, FormatAxis(series[2], i), 12, series[2].Brush, dpi, right + 10, y, TextAlignment.Left);
            if (series.Count > 1)
                Label(dc, FormatAxis(series[1], i, true), 12, series[1].Brush, dpi, right + 62, y, TextAlignment.Left);
        }

        // Axis rules like the concept: a coloured line beside each right-hand scale
        if (series.Count > 2)
            dc.DrawLine(ThinPen(series[2].Brush), new Point(right + 2, top), new Point(right + 2, bottom));
        if (series.Count > 1)
            dc.DrawLine(ThinPen(series[1].Brush), new Point(right + 54, top), new Point(right + 54, bottom));
        dc.DrawLine(ThinPen(Muted), new Point(left, top), new Point(left, bottom));
        dc.DrawLine(ThinPen(Muted), new Point(left, bottom), new Point(right, bottom));
    }

    private static string FormatAxis(ChartSeries s, int step, bool temperature = false)
    {
        double value = s.Min + (s.Max - s.Min) * step / Divisions;
        if (temperature && Units.Imperial) value = value * 9 / 5 + 32;
        return s.Max - s.Min >= 20
            ? value.ToString("#,##0", CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
