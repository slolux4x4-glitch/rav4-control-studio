using System.Globalization;

namespace RAV4ControlStudio.ViewModels;

/// <summary>Global metric / imperial preference and the conversions the app shows (speed, temperature, pressure, distance).</summary>
public static class Units
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool Imperial { get; set; }

    /// <summary>Formats a difference (no temperature offset) in the chosen unit system, with a sign.</summary>
    public static string FormatDelta(string unit, double delta, string format)
    {
        double v = delta;
        string u = unit;
        if (Imperial)
        {
            switch (unit)
            {
                case "km/h": v *= 0.621371; u = "mph"; break;
                case "°C": v *= 9.0 / 5; u = "°F"; break;
                case "kPa": v *= 0.145038; u = "psi"; break;
                case "km": v *= 0.621371; u = "mi"; break;
            }
        }
        string f = Math.Abs(v) < 10 ? "0.0" : "0";
        return ((v >= 0 ? "+" : "") + v.ToString(f, Inv) + " " + u).Trim();
    }

    public static string Format(string unit, double value, string format)
    {
        if (Imperial)
        {
            switch (unit)
            {
                case "km/h": return (value * 0.621371).ToString("0", Inv) + " mph";
                case "°C": return (value * 9 / 5 + 32).ToString("0", Inv) + " °F";
                case "kPa": return (value * 0.145038).ToString("0.0", Inv) + " psi";
                case "km": return (value * 0.621371).ToString("0", Inv) + " mi";
            }
        }
        return (value.ToString(format, Inv) + " " + unit).Trim();
    }
}
