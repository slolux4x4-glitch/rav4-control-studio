namespace Obd2.Core.Obd;

/// <summary>
/// Decodes Mode 01 PID 01 ("monitor status since DTCs cleared").
/// Byte A: bit 7 = MIL on, bits 0-6 = stored DTC count.
/// Byte B: bit 3 = ignition type (1 = compression, 0 = spark). Bits 0-2 = continuous monitors supported,
///         bits 4-6 = their status (1 = not complete), for both ignition types.
/// Bytes C (supported) and D (1 = not complete) cover the non-continuous monitors.
/// Spark-ignition mapping is standard. Compression-ignition mapping is best-effort.
/// </summary>
public static class ReadinessDecoder
{
    private static readonly string[] SparkNames =
    {
        "Catalyst", "Heated catalyst", "Evaporative system", "Secondary air system",
        "A/C refrigerant", "O2 sensor", "O2 sensor heater", "EGR / VVT system",
    };

    private static readonly string[] CompressionNames =
    {
        "NMHC catalyst", "NOx / SCR aftertreatment", "", "Boost pressure",
        "", "Exhaust gas sensor", "PM filter", "EGR / VVT system",
    };

    private static readonly string[] ContinuousNames = { "Misfire", "Fuel system", "Components" };

    public static ReadinessStatus Decode(byte[] d, DateTime timestamp)
    {
        if (d.Length < 4)
            throw new ArgumentException("PID 01 needs 4 data bytes.", nameof(d));

        bool mil = (d[0] & 0x80) != 0;
        int dtcCount = d[0] & 0x7F;
        bool compression = (d[1] & 0x08) != 0;

        var monitors = new List<MonitorStatus>();

        // The three continuous monitors are reported the same way for both ignition types.
        for (int i = 0; i < ContinuousNames.Length; i++)
        {
            bool supported = (d[1] & (1 << i)) != 0;
            bool complete = (d[1] & (1 << (i + 4))) == 0;
            monitors.Add(new MonitorStatus(ContinuousNames[i], supported, complete));
        }

        var names = compression ? CompressionNames : SparkNames;
        for (int bit = 0; bit < 8; bit++)
        {
            if (names[bit].Length == 0)
                continue;

            bool supported = (d[2] & (1 << bit)) != 0;
            bool incomplete = (d[3] & (1 << bit)) != 0;
            monitors.Add(new MonitorStatus(names[bit], supported, !incomplete));
        }

        return new ReadinessStatus(mil, dtcCount, compression, monitors, timestamp);
    }
}
