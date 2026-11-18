namespace Obd2.Core.Obd;

/// <summary>
/// Offline VIN decoder (ISO 3779 / 3780 / 49 CFR 565). Covers check digit, model year and a small
/// manufacturer table. It does not replace the full NHTSA vPIC database.
/// </summary>
public static class VinDecoder
{
    private const string AllowedChars = "ABCDEFGHJKLMNPRSTUVWXYZ0123456789";

    // Position 10 year codes, 2010-2039 (I, O, Q, U, Z, 0 are not used).
    private const string YearCodes = "ABCDEFGHJKLMNPRSTVWXY123456789";

    private static readonly int[] Weights = { 8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2 };

    public static VinInfo Decode(string rawVin)
    {
        var vin = (rawVin ?? "").Trim().ToUpperInvariant();
        bool lengthOk = vin.Length == 17 && vin.All(c => AllowedChars.Contains(c));

        bool checkOk = lengthOk && ComputeCheckDigit(vin) == vin[8];

        int? year = null;
        if (vin.Length >= 10)
        {
            int idx = YearCodes.IndexOf(vin[9]);
            if (idx >= 0) year = 2010 + idx;
        }

        return new VinInfo(
            vin,
            lengthOk,
            checkOk,
            year,
            Manufacturer(vin),
            Region(vin));
    }

    /// <summary>Computes the position-9 check digit for a 17-character VIN (position 9 is ignored).</summary>
    public static char ComputeCheckDigit(string vin)
    {
        int sum = 0;
        for (int i = 0; i < 17; i++)
        {
            if (i == 8) continue;
            sum += Transliterate(vin[i]) * Weights[i];
        }

        int remainder = sum % 11;
        return remainder == 10 ? 'X' : (char)('0' + remainder);
    }

    private static int Transliterate(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        return c switch
        {
            'A' or 'J' => 1,
            'B' or 'K' or 'S' => 2,
            'C' or 'L' or 'T' => 3,
            'D' or 'M' or 'U' => 4,
            'E' or 'N' or 'V' => 5,
            'F' or 'W' => 6,
            'G' or 'P' or 'X' => 7,
            'H' or 'Y' => 8,
            'R' or 'Z' => 9,
            _ => 0,
        };
    }

    private static string Manufacturer(string vin)
    {
        if (vin.Length < 3) return "Unknown";
        string wmi = vin[..3];

        if (wmi.StartsWith("JT", StringComparison.Ordinal)) return "Toyota (Japan)";
        if (wmi.StartsWith("2T", StringComparison.Ordinal)) return "Toyota (Canada)";
        if (wmi.StartsWith("4T", StringComparison.Ordinal) || wmi.StartsWith("5T", StringComparison.Ordinal))
            return "Toyota (USA)";
        if (wmi.StartsWith("SB1", StringComparison.Ordinal)) return "Toyota (UK)";
        return $"Unknown manufacturer (WMI {wmi})";
    }

    private static string Region(string vin)
    {
        if (vin.Length == 0) return "Unknown";
        return vin[0] switch
        {
            >= '1' and <= '5' => "North America",
            'J' or 'K' or 'L' or 'M' or 'N' or 'P' or 'R' => "Asia",
            'S' or 'T' or 'U' or 'V' or 'W' or 'X' or 'Y' or 'Z' => "Europe / Africa",
            _ => "Other",
        };
    }
}
