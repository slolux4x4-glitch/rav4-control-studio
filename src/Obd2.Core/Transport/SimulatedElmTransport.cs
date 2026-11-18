using System.Diagnostics;
using System.Text;
using Obd2.Core.Obd;

namespace Obd2.Core.Transport;

/// <summary>
/// Offline ECU simulator (DEMO MODE). Produces plausible engine data so the app can be run and
/// tested without a vehicle. It answers the same commands a real ELM327 + ECU would.
/// </summary>
public sealed class SimulatedElmTransport : IElmTransport
{
    private readonly bool _faults;

    public SimulatedElmTransport(bool simulateFaults = false) => _faults = simulateFaults;

    /// <summary>Synthetic VIN with a valid check digit, model year code 'M' (2021), Toyota (Japan) WMI.</summary>
    public static readonly string SimulatedVin = BuildVin("JTMP1RFV0MD012345");

    private static readonly int[] SupportedPids =
    {
        0x01, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11,
        0x14, 0x15, 0x1F, 0x21, 0x24, 0x2F, 0x31, 0x33, 0x3C, 0x42, 0x46, 0x5C,
    };

    private readonly Stopwatch _clock = new();
    private readonly Random _rng = new(7);
    private string _reply = "";
    private string _header = "7DF";

    private static readonly Dictionary<string, Dictionary<string, string>> Ecus = new()
    {
        ["7E0"] = new() { ["F190"] = SimulatedVin, ["F187"] = "89661-SIM00", ["F188"] = "SIM-ECM-SW1.0", ["F191"] = "HW-ECM-01" },
        ["7E1"] = new() { ["F190"] = SimulatedVin, ["F187"] = "89530-SIM01", ["F188"] = "SIM-CVT-SW2.3" },
        ["7B0"] = new() { ["F190"] = SimulatedVin, ["F187"] = "89540-SIM02", ["F188"] = "SIM-ABS-SW0.9" },
    };

    public string DisplayName => "DEMO MODE (simulated ECU)";

    public void Open() => _clock.Restart();

    public void WriteLine(string command) => _reply = Respond(command.Trim().ToUpperInvariant());

    public string ReadUntilPrompt(TimeSpan timeout)
    {
        Thread.Sleep(25); // roughly the latency of a real adapter
        var reply = _reply;
        _reply = "";
        return reply + "\r\r";
    }

    public void Dispose() { }

    private string Respond(string cmd)
    {
        if (cmd.StartsWith("ATSH", StringComparison.Ordinal))
        {
            _header = cmd[4..];
            return "OK\r";
        }

        if (_header != "7DF" && (cmd == "3E00" || cmd.StartsWith("22", StringComparison.Ordinal)))
        {
            if (!Ecus.TryGetValue(_header, out var dids)) return "NO DATA\r";
            if (cmd == "3E00") return "7E00\r";

            string did = cmd[2..];
            return dids.TryGetValue(did, out var text)
                ? "62" + did + Hex(Encoding.ASCII.GetBytes(text)) + "\r"
                : "7F2231\r";
        }

        if (cmd.StartsWith("AT", StringComparison.Ordinal))
        {
            return cmd switch
            {
                "ATZ" => "\rELM327 v1.5\r\r",
                "ATI" => "ELM327 v1.5",
                "ATDP" => "ISO 15765-4 (CAN 11/500)",
                "ATRV" => "14.2V",
                "AT@1" => "OBDII to RS232 Interpreter",
                "AT@2" => "SIM-ELM-0001",
                _ => "OK\r",
            };
        }

        if (cmd == "03") return _faults ? "43042000000000\r" : "43000000000000\r";
        if (cmd == "07") return _faults ? "47030100000000\r" : "47000000000000\r";
        if (cmd == "0902") return VinFrames();
        if (cmd == "0A") return "4A00000000000000" + "\r";
        if (cmd.Length == 6 && cmd.StartsWith("02", StringComparison.Ordinal) && cmd.EndsWith("00", StringComparison.Ordinal))
        {
            if (!_faults) return "NO DATA\r";
            byte fpid = Convert.ToByte(cmd.Substring(2, 2), 16);
            if (fpid == 0x02) return "4202000420\r";

            string live = Mode01(fpid);
            return live.StartsWith("41", StringComparison.Ordinal) ? "42" + cmd.Substring(2, 2) + "00" + live[4..] : "NO DATA\r";
        }
        if (cmd == "0900") return "490054400000\r";
        if (cmd == "0904") return "490401" + Ascii("F1B3D000", 16) + "\r";
        if (cmd == "0906") return "490601A1B2C3D4\r";
        if (cmd == "090A") return "490A01" + Ascii("ECM-ENGINE", 20) + "\r";

        if (cmd.Length == 4 && cmd.StartsWith("01", StringComparison.Ordinal))
        {
            byte pid = Convert.ToByte(cmd[2..], 16);
            return Mode01(pid);
        }

        return "?\r";
    }

    private string Mode01(byte pid)
    {
        if (pid % 0x20 == 0)
            return $"41{pid:X2}{SupportMask(pid)}\r";

        if (!SupportedPids.Contains(pid))
            return "NO DATA\r";

        double t = _clock.Elapsed.TotalSeconds;
        double throttle = Math.Max(0, Math.Sin(t / 9.0)) * 35 + 0.6; // 0-35 %, periodic pulses
        double rpm = 740 + throttle * 62 + _rng.NextDouble() * 24;
        double speed = Math.Max(0, (throttle - 3) * 2.2);
        double coolant = Math.Min(90, 32 + t * 0.45);
        double voltage = 14.2 + Math.Sin(t) * 0.05;

        byte[] data = pid switch
        {
            // MIL off, 0 DTCs, spark ignition; misfire/fuel/components complete; catalyst, evap, O2 done.
            0x01 => new byte[] { (byte)(_faults ? 0x81 : 0x00), 0x07, 0xE5, 0x00 },
            0x04 => new[] { Pct(18 + throttle * 1.5) },
            0x05 => new[] { Temp(coolant) },
            0x06 => new[] { FuelTrim(1.6) },
            0x07 => new[] { FuelTrim(-2.3) },
            0x08 => new[] { FuelTrim(-0.8) },
            0x09 => new[] { FuelTrim(1.2) },
            0x14 => new[] { (byte)Math.Clamp((0.45 + 0.35 * Math.Sin(t * 2)) / 0.005, 0, 255), (byte)0xFF },
            0x15 => new[] { (byte)Math.Clamp((0.62 + 0.05 * Math.Sin(t * 0.7)) / 0.005, 0, 255), (byte)0xFF },
            0x24 => Word((1.0 + 0.03 * Math.Sin(t * 2)) * 32768).Concat(Word((3.0 + 0.2 * Math.Sin(t * 2)) * 8192)).ToArray(),
            0x3C => Word((Math.Min(700, 380 + t * 1.5) + 40) * 10),
            0x0A => new[] { (byte)(58 / 3) },
            0x0B => new[] { (byte)(35 + throttle * 1.2) },
            0x0C => Word(rpm * 4),
            0x0D => new[] { (byte)speed },
            0x0E => new[] { (byte)((12 + throttle * 0.1 + 64) * 2) },
            0x0F => new[] { Temp(28 + t * 0.02) },
            0x10 => Word((2.5 + throttle * 0.8) * 100),
            0x11 => new[] { Pct(throttle) },
            0x1F => Word(t),
            0x21 => Word(0),
            0x2F => new[] { Pct(62) },
            0x31 => Word(12345),
            0x33 => new[] { (byte)99 },
            0x42 => Word(voltage * 1000),
            0x46 => new[] { Temp(24) },
            0x5C => new[] { Temp(Math.Min(95, coolant - 5)) },
            _ => Array.Empty<byte>(),
        };

        return "41" + pid.ToString("X2") + Hex(data) + "\r";
    }

    private static string SupportMask(byte block)
    {
        uint mask = 0;
        for (int pid = block + 1; pid <= block + 0x20; pid++)
        {
            bool supported = SupportedPids.Contains(pid) || (pid == block + 0x20 && HasNextBlock(block));
            if (supported)
                mask |= 1u << (32 - (pid - block));
        }

        return mask.ToString("X8");
    }

    private static bool HasNextBlock(byte block) =>
        SupportedPids.Any(p => p > block + 0x20);

    private string VinFrames()
    {
        // Simulate ISO-TP framing: header 49 02 01 followed by 17 ASCII bytes, split over lines.
        var bytes = new List<byte> { 0x49, 0x02, 0x01 };
        bytes.AddRange(Encoding.ASCII.GetBytes(SimulatedVin));

        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Count; i += 7)
        {
            var chunk = bytes.Skip(i).Take(7).ToArray();
            sb.Append($"{i / 7}: {Hex(chunk)}\r");
        }
        return sb.ToString();
    }

    private static string Ascii(string text, int size) =>
        Hex(Encoding.ASCII.GetBytes(text.PadRight(size, '\0')));

    private static byte Pct(double percent) => (byte)Math.Clamp(Math.Round(percent * 255 / 100), 0, 255);

    private static byte Temp(double celsius) => (byte)Math.Clamp(Math.Round(celsius + 40), 0, 255);

    private static byte FuelTrim(double percent) => (byte)Math.Clamp(Math.Round(percent * 128 / 100 + 128), 0, 255);

    private static byte[] Word(double value)
    {
        int raw = (int)Math.Clamp(Math.Round(value), 0, 65535);
        return new[] { (byte)(raw >> 8), (byte)(raw & 0xFF) };
    }

    private static string Hex(IEnumerable<byte> bytes) => string.Concat(bytes.Select(b => b.ToString("X2")));

    private static string BuildVin(string vinWithoutCheckDigit)
    {
        char check = VinDecoder.ComputeCheckDigit(vinWithoutCheckDigit);
        return vinWithoutCheckDigit[..8] + check + vinWithoutCheckDigit[9..];
    }
}
