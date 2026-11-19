using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RAV4ControlStudio.Controls;

/// <summary>
/// Round 270° dial: bezel, scale with major and minor ticks and numbers, a glowing value arc, optional warning
/// and danger zones at the top of the scale, and a large centre readout. Value NaN shows "--".
/// Everything except the value arc and readout is drawn once into a cached drawing and reused between frames.
/// </summary>
public sealed class GaugeControl : FrameworkElement
{
    private static FrameworkPropertyMetadata Render(object defaultValue) =>
        new(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(GaugeControl), Render(double.NaN));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(GaugeControl), Render(0.0));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(GaugeControl), Render(100.0));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(GaugeControl), Render(""));
    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(nameof(ValueFormat), typeof(string), typeof(GaugeControl), Render("0"));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(GaugeControl), Render(Brushes.DeepSkyBlue));

    /// <summary>Distance between numbered ticks, in the gauge's units. 0 picks about ten steps.</summary>
    public static readonly DependencyProperty MajorStepProperty = DependencyProperty.Register(nameof(MajorStep), typeof(double), typeof(GaugeControl), Render(0.0));
    /// <summary>Scale numbers are divided by this (1000 shows rpm as 0.5, 1.0, ...).</summary>
    public static readonly DependencyProperty LabelDivisorProperty = DependencyProperty.Register(nameof(LabelDivisor), typeof(double), typeof(GaugeControl), Render(1.0));
    public static readonly DependencyProperty LabelFormatProperty = DependencyProperty.Register(nameof(LabelFormat), typeof(string), typeof(GaugeControl), Render("0"));
    /// <summary>Small text in the gap at the bottom of the dial, for example "×1000 rpm".</summary>
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(GaugeControl), Render(""));
    public static readonly DependencyProperty WarningFromProperty = DependencyProperty.Register(nameof(WarningFrom), typeof(double), typeof(GaugeControl), Render(double.NaN));
    public static readonly DependencyProperty DangerFromProperty = DependencyProperty.Register(nameof(DangerFrom), typeof(double), typeof(GaugeControl), Render(double.NaN));
    /// <summary>Optional picture under the readout (the coolant symbol).</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(ImageSource), typeof(GaugeControl), Render(null!));

    // Kept so existing XAML still loads; the unit is always shown under the value now.
    public static readonly DependencyProperty UnitBelowProperty = DependencyProperty.Register(nameof(UnitBelow), typeof(bool), typeof(GaugeControl), Render(true));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string ValueFormat { get => (string)GetValue(ValueFormatProperty); set => SetValue(ValueFormatProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public double MajorStep { get => (double)GetValue(MajorStepProperty); set => SetValue(MajorStepProperty, value); }
    public double LabelDivisor { get => (double)GetValue(LabelDivisorProperty); set => SetValue(LabelDivisorProperty, value); }
    public string LabelFormat { get => (string)GetValue(LabelFormatProperty); set => SetValue(LabelFormatProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public double WarningFrom { get => (double)GetValue(WarningFromProperty); set => SetValue(WarningFromProperty, value); }
    public double DangerFrom { get => (double)GetValue(DangerFromProperty); set => SetValue(DangerFromProperty, value); }
    public ImageSource? Icon { get => (ImageSource?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public bool UnitBelow { get => (bool)GetValue(UnitBelowProperty); set => SetValue(UnitBelowProperty, value); }

    // Arc runs from 225° (lower left) clockwise over the top to -45° (lower right).
    private const double StartAngle = 225;
    private const double SweepAngle = 270;

    private static readonly Brush WarnBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush DangerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x2C, 0x41)));
    private static readonly Brush TickBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6F, 0x88, 0xA0)));
    private static readonly Brush ScaleTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC9, 0xD6, 0xE2)));
    private static readonly Pen RimPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x4A, 0x70)), 1.5));
    private static readonly Pen InnerRimPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x2A, 0xA7, 0xF5)), 1));
    private static readonly Brush FaceBrush = Frozen(new RadialGradientBrush(Color.FromRgb(0x0B, 0x19, 0x27), Color.FromRgb(0x06, 0x0F, 0x18)));

    private Drawing? _static;
    private Size _staticSize;
    private Brush? _text, _muted;
    private Brush? _glowSource, _glow, _fill;
    private Pen? _glowPen, _fillPen;
    private readonly TextCache _labels = new();

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != ValueProperty) _static = null; // the scale depends on everything but the value
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;

        double r = Math.Min(w, h) / 2 - 2;
        double cx = w / 2, cy = h / 2;
        double band = r * 0.86, thick = Math.Max(4, r * 0.085);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_static is null || _staticSize != new Size(w, h))
        {
            _static = BuildStatic(cx, cy, r, band, thick, dpi);
            _staticSize = new Size(w, h);
        }
        dc.DrawDrawing(_static);

        bool hasValue = !double.IsNaN(Value);
        if (hasValue && Maximum > Minimum)
        {
            // The value arc stops where a coloured zone begins, so the zone stays readable.
            double limit = Maximum;
            if (!double.IsNaN(WarningFrom)) limit = Math.Min(limit, WarningFrom);
            else if (!double.IsNaN(DangerFrom)) limit = Math.Min(limit, DangerFrom);
            double end = Math.Min(Value, limit);
            if (end > Minimum)
            {
                var (glow, fill) = ValueBrushes();
                double a1 = Angle(end);
                DrawArc(dc, PenCache.Get(ref _glowPen, glow, thick * 2.4), cx, cy, band, StartAngle, a1);
                DrawArc(dc, PenCache.Get(ref _fillPen, fill, thick), cx, cy, band, StartAngle, a1);
            }
        }

        var text = _text ??= (Brush)(TryFindResource("TextBrush") ?? Brushes.White);
        var muted = _muted ??= (Brush)(TryFindResource("MutedBrush") ?? Brushes.Gray);

        // Readout turns orange or red when the value is inside a zone.
        Brush valueBrush = text;
        if (hasValue && !double.IsNaN(DangerFrom) && Value >= DangerFrom) valueBrush = DangerBrush;
        else if (hasValue && !double.IsNaN(WarningFrom) && Value >= WarningFrom) valueBrush = WarnBrush;

        string valueText = hasValue ? Value.ToString(ValueFormat, CultureInfo.InvariantCulture) : "--";
        var big = _labels.Get(valueText, r * 0.34, valueBrush, dpi, FontWeights.Bold);
        double top = cy - big.Height * 0.62;
        dc.DrawText(big, new Point(cx - big.Width / 2, top));

        if (!string.IsNullOrEmpty(Unit))
        {
            var unit = _labels.Get(Unit, r * 0.15, muted, dpi, FontWeights.Normal);
            dc.DrawText(unit, new Point(cx - unit.Width / 2, top + big.Height * 0.92));
        }
    }

    private Drawing BuildStatic(double cx, double cy, double r, double band, double thick, double dpi)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            // Bezel and face
            dc.DrawEllipse(FaceBrush, RimPen, new Point(cx, cy), r, r);
            dc.DrawEllipse(null, InnerRimPen, new Point(cx, cy), r * 0.955, r * 0.955);

            // Track
            DrawArc(dc, new Pen(TrackBrush, thick), cx, cy, band, StartAngle, StartAngle - SweepAngle);

            // Warning and danger zones, always lit like a rev counter's red line
            if (Maximum > Minimum)
            {
                double warn = double.IsNaN(WarningFrom) ? double.NaN : Math.Clamp(WarningFrom, Minimum, Maximum);
                double danger = double.IsNaN(DangerFrom) ? double.NaN : Math.Clamp(DangerFrom, Minimum, Maximum);
                if (!double.IsNaN(warn))
                {
                    double warnEnd = double.IsNaN(danger) ? Maximum : danger;
                    if (warnEnd > warn) Zone(dc, WarnBrush, cx, cy, band, thick, warn, warnEnd);
                }
                if (!double.IsNaN(danger) && Maximum > danger)
                    Zone(dc, DangerBrush, cx, cy, band, thick, danger, Maximum);
            }

            DrawScale(dc, cx, cy, r, band, thick, dpi);

            if (!string.IsNullOrEmpty(Caption) && r > 60)
            {
                var caption = _labels.Get(Caption, r * 0.085, TickBrush, dpi, FontWeights.Normal);
                dc.DrawText(caption, new Point(cx - caption.Width / 2, cy + r * 0.66));
            }

            if (Icon is not null)
            {
                double size = r * 0.32;
                dc.DrawImage(Icon, new Rect(cx - size / 2, cy + r * 0.40, size, size));
            }
        }
        group.Freeze();
        return group;
    }

    private void DrawScale(DrawingContext dc, double cx, double cy, double r, double band, double thick, double dpi)
    {
        double range = Maximum - Minimum;
        if (range <= 0) return;

        double step = MajorStep > 0 ? MajorStep : NiceStep(range / 10);
        int majors = (int)Math.Floor(range / step + 1e-9);
        if (majors > 40) return; // a bad step would draw a solid ring of ticks

        int minors = r > 90 ? 4 : 1;
        var majorPen = new Pen(TickBrush, Math.Max(1, r * 0.012)); majorPen.Freeze();
        var minorPen = new Pen(TickBrush, Math.Max(0.6, r * 0.006)); minorPen.Freeze();

        double inner = band - thick / 2 - r * 0.02;  // ticks hang inside the track
        double labelRadius = inner - r * 0.20;
        double fontSize = r * 0.105;
        bool labels = fontSize >= 7.5;
        double divisor = LabelDivisor == 0 ? 1 : LabelDivisor;

        for (int i = 0; i <= majors; i++)
        {
            double v = Minimum + i * step;
            double a = Angle(v);
            dc.DrawLine(majorPen, PointOnCircle(cx, cy, inner, a), PointOnCircle(cx, cy, inner - r * 0.09, a));

            if (labels)
            {
                var label = _labels.Get((v / divisor).ToString(LabelFormat, CultureInfo.InvariantCulture), fontSize, ScaleTextBrush, dpi, FontWeights.Normal);
                var p = PointOnCircle(cx, cy, labelRadius, a);
                dc.DrawText(label, new Point(p.X - label.Width / 2, p.Y - label.Height / 2));
            }

            if (i == majors) break;
            for (int m = 1; m <= minors; m++)
            {
                double mv = v + step * m / (minors + 1);
                if (mv > Maximum) break;
                double ma = Angle(mv);
                dc.DrawLine(minorPen, PointOnCircle(cx, cy, inner, ma), PointOnCircle(cx, cy, inner - r * 0.045, ma));
            }
        }
    }

    private void Zone(DrawingContext dc, Brush brush, double cx, double cy, double band, double thick, double from, double to)
    {
        var glow = brush.Clone(); glow.Opacity = 0.22; glow.Freeze();
        DrawArc(dc, new Pen(glow, thick * 2.4), cx, cy, band, Angle(from), Angle(to));
        DrawArc(dc, new Pen(brush, thick), cx, cy, band, Angle(from), Angle(to));
    }

    // A soft halo and a light-to-full gradient of the accent colour, rebuilt only when the accent changes.
    private (Brush Glow, Brush Fill) ValueBrushes()
    {
        if (_glow is null || !ReferenceEquals(_glowSource, Accent))
        {
            var color = Accent is SolidColorBrush s ? s.Color : Colors.DeepSkyBlue;
            _glow = Frozen(new SolidColorBrush(color) { Opacity = 0.22 });
            var light = Color.FromRgb((byte)((color.R + 255) / 2), (byte)((color.G + 255) / 2), (byte)((color.B + 255) / 2));
            _fill = Frozen(new LinearGradientBrush(color, light, new Point(0, 1), new Point(1, 0)));
            _glowSource = Accent;
        }
        return (_glow, _fill!);
    }

    private double Angle(double value)
    {
        double f = Maximum > Minimum ? Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1) : 0;
        return StartAngle - SweepAngle * f;
    }

    private static double NiceStep(double raw)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * mag;
    }

    private static void DrawArc(DrawingContext dc, Pen pen, double cx, double cy, double r, double startDeg, double endDeg)
    {
        double sweep = Math.Abs(startDeg - endDeg);
        if (sweep < 0.01) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOnCircle(cx, cy, r, startDeg), isFilled: false, isClosed: false);
            ctx.ArcTo(PointOnCircle(cx, cy, r, endDeg), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(double cx, double cy, double r, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy - r * Math.Sin(rad));
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
