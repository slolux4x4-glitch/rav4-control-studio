using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RAV4ControlStudio.Controls;

/// <summary>
/// Reuses laid-out text between frames. Gauges and charts redraw five times a second, but most of their labels
/// (axis values, units) never change, so measuring them again each frame is wasted work.
/// </summary>
internal sealed class TextCache
{
    private const int Limit = 128; // time labels keep changing; start afresh rather than grow forever

    private readonly Dictionary<(string Text, double Size, Brush Brush, FontWeight Weight), FormattedText> _items = new();
    private double _dpi;

    public static readonly FontFamily Font = new("Segoe UI");

    public FormattedText Get(string text, double size, Brush brush, double dpi, FontWeight? weight = null)
    {
        var w = weight ?? FontWeights.Normal;
        if (dpi != _dpi || _items.Count > Limit)
        {
            _items.Clear();
            _dpi = dpi;
        }

        var key = (text, size, brush, w);
        if (_items.TryGetValue(key, out var cached))
            return cached;

        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Font, FontStyles.Normal, w, FontStretches.Normal), size, brush, dpi);
        return _items[key] = formatted;
    }
}

internal static class PenCache
{
    /// <summary>Returns the cached pen when brush and thickness are unchanged, otherwise a new frozen one.</summary>
    public static Pen Get(ref Pen? cache, Brush brush, double thickness, bool roundCaps = false)
    {
        if (cache is not null && ReferenceEquals(cache.Brush, brush) && cache.Thickness == thickness)
            return cache;

        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
        if (roundCaps)
        {
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
        }
        pen.Freeze();
        return cache = pen;
    }
}
