using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RAV4ControlStudio.Controls;

/// <summary>270° arc gauge with a centre readout. Value NaN shows "--".</summary>
public sealed class GaugeControl : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(GaugeControl),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(GaugeControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(GaugeControl),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(GaugeControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(
        nameof(ValueFormat), typeof(string), typeof(GaugeControl),
        new FrameworkPropertyMetadata("0", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(GaugeControl),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitBelowProperty = DependencyProperty.Register(
        nameof(UnitBelow), typeof(bool), typeof(GaugeControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool UnitBelow { get => (bool)GetValue(UnitBelowProperty); set => SetValue(UnitBelowProperty, value); }

    // Arc runs from 225° (lower left) clockwise over the top to -45° (lower right).
    private const double StartAngle = 225;
    private const double SweepAngle = 270;

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string ValueFormat { get => (string)GetValue(ValueFormatProperty); set => SetValue(ValueFormatProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    // Theme brushes, pens and text reused between frames (the gauges redraw five times a second).
    private Brush? _track, _text, _muted;
    private Pen? _trackPen, _valuePen;
    private readonly TextCache _labels = new();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;

        var track = _track ??= (Brush)(TryFindResource("BorderBrush2") ?? Brushes.DimGray);
        double stroke = Math.Max(6, Math.Min(w, h) * 0.07);
        double radius = Math.Min(w * 0.46, h * 0.46) - stroke / 2;
        double cx = w / 2;
        double cy = h * 0.52;

        var trackPen = PenCache.Get(ref _trackPen, track, stroke, roundCaps: true);
        DrawArc(dc, trackPen, cx, cy, radius, StartAngle, StartAngle - SweepAngle);

        double fraction = 0;
        bool hasValue = !double.IsNaN(Value);
        if (hasValue && Maximum > Minimum)
            fraction = Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1);

        if (hasValue && fraction > 0)
        {
            var valuePen = PenCache.Get(ref _valuePen, Accent, stroke, roundCaps: true);
            DrawArc(dc, valuePen, cx, cy, radius, StartAngle, StartAngle - SweepAngle * fraction);
        }

        var text = _text ??= (Brush)(TryFindResource("TextBrush") ?? Brushes.White);
        var muted = _muted ??= (Brush)(TryFindResource("MutedBrush") ?? Brushes.Gray);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        string valueText = hasValue ? Value.ToString(ValueFormat, CultureInfo.InvariantCulture) : "--";
        var valueFormatted = Text(valueText, radius * 0.5, text, dpi, FontWeights.SemiBold);

        if (string.IsNullOrEmpty(Unit))
        {
            dc.DrawText(valueFormatted, new Point(cx - valueFormatted.Width / 2, cy - valueFormatted.Height * 0.55));
        }
        else if (UnitBelow)
        {
            dc.DrawText(valueFormatted, new Point(cx - valueFormatted.Width / 2, cy - valueFormatted.Height * 0.72));
            var unitFormatted = Text(Unit, radius * 0.24, muted, dpi, FontWeights.Normal);
            dc.DrawText(unitFormatted, new Point(cx - unitFormatted.Width / 2, cy + radius * 0.12));
        }
        else
        {
            // value and unit side by side, unit smaller and on the baseline (as in the concept)
            var unitFormatted = Text(Unit, radius * 0.3, muted, dpi, FontWeights.Normal);
            double total = valueFormatted.Width + 4 + unitFormatted.Width;
            double x = cx - total / 2;
            double top = cy - valueFormatted.Height * 0.55;
            dc.DrawText(valueFormatted, new Point(x, top));
            dc.DrawText(unitFormatted, new Point(x + valueFormatted.Width + 4, top + valueFormatted.Height - unitFormatted.Height - radius * 0.04));
        }
    }

    private static void DrawArc(DrawingContext dc, Pen pen, double cx, double cy, double r, double startDeg, double endDeg)
    {
        var start = PointOnCircle(cx, cy, r, startDeg);
        var end = PointOnCircle(cx, cy, r, endDeg);
        double sweep = Math.Abs(startDeg - endDeg);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(end, new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(double cx, double cy, double r, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy - r * Math.Sin(rad));
    }

    private FormattedText Text(string text, double size, Brush brush, double dpi, FontWeight weight) =>
        _labels.Get(text, size, brush, dpi, weight);
}
