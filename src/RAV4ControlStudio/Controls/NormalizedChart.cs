using System.Windows;
using System.Windows.Media;
using RAV4ControlStudio.ViewModels;

namespace RAV4ControlStudio.Controls;

/// <summary>
/// Multi-line graph where each line is scaled to its own Min/Max (shown as 0 to 100 %), so parameters with
/// different units can be compared by shape. Uses the same fixed 3-minute window as the Overview graph.
/// </summary>
public sealed class NormalizedChart : ChartBase
{
    private const int Divisions = 4;

    public Func<IEnumerable<ChartSeries>>? Provider { get; set; }

    protected override Rect PlotArea(double width, double height) =>
        new(new Point(52, 10), new Point(Math.Max(52, width - 14), height - 26));

    protected override IEnumerable<ChartSeries> SeriesToDraw() =>
        Provider?.Invoke() ?? Enumerable.Empty<ChartSeries>();

    protected override void DrawStatic(DrawingContext dc, Rect plot, double dpi)
    {
        for (int i = 0; i <= Divisions; i++)
        {
            double y = plot.Bottom - plot.Height * i / Divisions;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(dc, $"{i * 100 / Divisions}%", 12, Muted, dpi, plot.Left - 8, y, TextAlignment.Right);
        }
        dc.DrawLine(ThinPen(Muted), new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        dc.DrawLine(ThinPen(Muted), new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
    }
}
