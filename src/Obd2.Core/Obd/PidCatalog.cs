namespace Obd2.Core.Obd;

/// <summary>
/// A Mode 01 parameter (SAE J1979). <see cref="ByteCount"/> is the number of data bytes after "41 PID".
/// <see cref="Decode"/> converts those bytes to engineering units using the standard formula.
/// </summary>
public sealed record PidDefinition(
    byte Pid,
    string Name,
    string Unit,
    string Group,
    int ByteCount,
    double Min,
    double Max,
    Func<byte[], double> Decode,
    string Format = "0.#")
{
    public string PidHex => $"01{Pid:X2}";

    public string Display(double value) => $"{value.ToString(Format, System.Globalization.CultureInfo.InvariantCulture)} {Unit}".Trim();
}

public static class PidCatalog
{
    public static readonly IReadOnlyList<PidDefinition> All = new[]
    {
        new PidDefinition(0x04, "Engine load", "%", "Engine", 1, 0, 100, d => d[0] * 100.0 / 255),
        new PidDefinition(0x05, "Coolant temperature", "°C", "Engine", 1, -40, 215, d => d[0] - 40, "0"),
        new PidDefinition(0x06, "Short-term fuel trim (B1)", "%", "Fuel", 1, -100, 99.2, d => (d[0] - 128) * 100.0 / 128),
        new PidDefinition(0x07, "Long-term fuel trim (B1)", "%", "Fuel", 1, -100, 99.2, d => (d[0] - 128) * 100.0 / 128),
        new PidDefinition(0x08, "Short-term fuel trim (B2)", "%", "Fuel", 1, -100, 99.2, d => (d[0] - 128) * 100.0 / 128),
        new PidDefinition(0x09, "Long-term fuel trim (B2)", "%", "Fuel", 1, -100, 99.2, d => (d[0] - 128) * 100.0 / 128),
        new PidDefinition(0x0A, "Fuel pressure (gauge)", "kPa", "Fuel", 1, 0, 765, d => d[0] * 3.0, "0"),
        new PidDefinition(0x0B, "Intake manifold pressure", "kPa", "Air", 1, 0, 255, d => d[0], "0"),
        new PidDefinition(0x0C, "Engine speed", "rpm", "Engine", 2, 0, 8000, d => (d[0] * 256 + d[1]) / 4.0, "0"),
        new PidDefinition(0x0D, "Vehicle speed", "km/h", "Engine", 1, 0, 255, d => d[0], "0"),
        new PidDefinition(0x0E, "Timing advance", "°", "Engine", 1, -64, 63.5, d => d[0] / 2.0 - 64),
        new PidDefinition(0x0F, "Intake air temperature", "°C", "Air", 1, -40, 215, d => d[0] - 40, "0"),
        new PidDefinition(0x10, "MAF air flow rate", "g/s", "Air", 2, 0, 655.35, d => (d[0] * 256 + d[1]) / 100.0, "0.00"),
        new PidDefinition(0x11, "Throttle position", "%", "Air", 1, 0, 100, d => d[0] * 100.0 / 255),
        new PidDefinition(0x14, "O2 sensor voltage (B1S1)", "V", "Air", 2, 0, 1.275, d => d[0] * 0.005, "0.000"),
        new PidDefinition(0x15, "O2 sensor voltage (B1S2)", "V", "Air", 2, 0, 1.275, d => d[0] * 0.005, "0.000"),
        new PidDefinition(0x1F, "Run time since start", "s", "Engine", 2, 0, 65535, d => d[0] * 256 + d[1], "0"),
        new PidDefinition(0x21, "Distance with MIL on", "km", "Diagnostics", 2, 0, 65535, d => d[0] * 256 + d[1], "0"),
        new PidDefinition(0x24, "Air-fuel ratio lambda (B1S1)", "λ", "Air", 4, 0, 2, d => (d[0] * 256 + d[1]) * 2.0 / 65536, "0.000"),
        new PidDefinition(0x2F, "Fuel tank level", "%", "Fuel", 1, 0, 100, d => d[0] * 100.0 / 255),
        new PidDefinition(0x31, "Distance since codes cleared", "km", "Diagnostics", 2, 0, 65535, d => d[0] * 256 + d[1], "0"),
        new PidDefinition(0x33, "Barometric pressure", "kPa", "Air", 1, 0, 255, d => d[0], "0"),
        new PidDefinition(0x3C, "Catalyst temperature (B1S1)", "°C", "Exhaust", 2, -40, 1000, d => (d[0] * 256 + d[1]) / 10.0 - 40, "0"),
        new PidDefinition(0x42, "Control module voltage", "V", "Electrical", 2, 0, 65.535, d => (d[0] * 256 + d[1]) / 1000.0, "0.00"),
        new PidDefinition(0x46, "Ambient air temperature", "°C", "Air", 1, -40, 215, d => d[0] - 40, "0"),
        new PidDefinition(0x5C, "Engine oil temperature", "°C", "Engine", 1, -40, 210, d => d[0] - 40, "0"),
    };

    /// <summary>PIDs polled every cycle for the Overview gauges.</summary>
    public static readonly byte[] DashboardPids = { 0x0C, 0x05, 0x42, 0x11 };

    public static PidDefinition? Find(byte pid) => All.FirstOrDefault(p => p.Pid == pid);
}
