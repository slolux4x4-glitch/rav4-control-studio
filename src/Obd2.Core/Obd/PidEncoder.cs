namespace Obd2.Core.Obd;

/// <summary>Inverse of <see cref="PidDefinition.Decode"/>: turns an engineering value back into raw PID bytes (used for log replay).</summary>
public static class PidEncoder
{
    public static byte[] Encode(PidDefinition def, double value)
    {
        int maxRaw = def.ByteCount == 1 ? 255 : 65535;

        double Decode(int raw)
        {
            var bytes = new byte[Math.Max(def.ByteCount, 1)];
            if (def.ByteCount == 1) bytes[0] = (byte)raw;
            else { bytes[0] = (byte)(raw >> 8); bytes[1] = (byte)(raw & 0xFF); }
            return def.Decode(bytes);
        }

        // All standard PID formulas increase with the raw value, so a binary search finds the closest raw code.
        int lo = 0, hi = maxRaw;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Decode(mid) < value) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && Math.Abs(Decode(lo - 1) - value) < Math.Abs(Decode(lo) - value)) lo--;

        var result = new byte[def.ByteCount];
        if (def.ByteCount == 1) result[0] = (byte)lo;
        else { result[0] = (byte)(lo >> 8); result[1] = (byte)(lo & 0xFF); }
        return result;
    }
}
