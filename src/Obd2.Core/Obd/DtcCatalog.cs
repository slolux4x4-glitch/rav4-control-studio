namespace Obd2.Core.Obd;

/// <summary>Decodes 2-byte DTC words (SAE J2012) into codes such as P0420.</summary>
public static class DtcCatalog
{
    public static string Format(byte first, byte second)
    {
        char type = (first >> 6) switch
        {
            0 => 'P', // powertrain
            1 => 'C', // chassis
            2 => 'B', // body
            _ => 'U', // network
        };

        int digit1 = (first >> 4) & 0x3;
        int digit2 = first & 0xF;
        int digit3 = (second >> 4) & 0xF;
        int digit4 = second & 0xF;

        return $"{type}{digit1}{digit2:X}{digit3:X}{digit4:X}";
    }

    public static string Describe(string code)
    {
        var entry = DtcDatabase.Find(code);
        if (entry is not null) return entry.Description;

        return code.Length >= 2 && (code[1] == '1' || code[1] == '3')
            ? "Manufacturer-specific code. Check Toyota service data for the meaning."
            : "Code not in the built-in database. Check service data.";
    }

    public static DtcInfo Create(byte first, byte second, DtcStatus status)
    {
        var code = Format(first, second);
        return new DtcInfo(code, Describe(code), status);
    }
}
