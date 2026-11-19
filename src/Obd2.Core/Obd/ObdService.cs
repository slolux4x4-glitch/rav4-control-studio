using Obd2.Core.Elm;
using Obd2.Core.Transport;

namespace Obd2.Core.Obd;

/// <summary>
/// High-level, read-only OBD-II operations on top of an ELM327 adapter:
/// initialisation, supported PIDs, live PIDs, stored/pending DTCs, I/M readiness and VIN.
/// </summary>
public sealed class ObdService : IDisposable
{
    private readonly ElmClient _elm;
    private readonly HashSet<byte> _supported = new();

    public ObdService(IElmTransport transport, SessionTrace? trace = null)
    {
        _elm = new ElmClient(transport, trace);
        _elm.Traffic += entry => Traffic?.Invoke(entry);
    }

    public event Action<TrafficEntry>? Traffic;

    public string TransportName => _elm.TransportName;

    public string AdapterId { get; private set; } = "";

    public string Protocol { get; private set; } = "";

    public string? Vin { get; private set; }

    public IReadOnlySet<byte> SupportedPids => _supported;

    public EcuIdentity? Identity { get; private set; }

    /// <summary>Resets and configures the adapter. protocolNumber: 0 = auto, 6 = ISO 15765-4 CAN 11-bit 500 kbaud.</summary>
    public void Initialize(int protocolNumber)
    {
        var reset = _elm.SendAt("ATZ", TimeSpan.FromSeconds(5));
        AdapterId = reset.Lines.FirstOrDefault(l => l.Contains("ELM", StringComparison.OrdinalIgnoreCase))
                    ?? (reset.Lines.FirstOrDefault() ?? "Unknown adapter");

        _elm.SendAt("ATE0");   // echo off
        _elm.SendAt("ATL0");   // linefeeds off
        _elm.SendAt("ATS0");   // no spaces in hex replies
        _elm.SendAt("ATH0");   // headers off
        _elm.SendAt("ATCAF1"); // automatic ISO-TP framing (multi-frame replies)
        _elm.SendAt($"ATSP{protocolNumber}");

        var describe = _elm.SendAt("ATDP");
        Protocol = describe.Lines.FirstOrDefault(l => !l.Equals("OK", StringComparison.OrdinalIgnoreCase)) ?? "Unknown";
        IsCan = Protocol.Contains("15765", StringComparison.Ordinal) || Protocol.Contains("CAN", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True on CAN (ISO 15765-4), as on the 2021 RAV4. Fault-code replies on CAN carry a count byte after the
    /// service byte ("43 01 04 20" = one code, P0420) that the older protocols do not have.
    /// </summary>
    public bool IsCan { get; private set; }

    /// <summary>Reads the supported PID bitmaps (0100, 0120, 0140, ...) and the VIN.</summary>
    public void LoadEcuInfo()
    {
        _supported.Clear();
        byte block = 0x00;

        while (block <= 0xE0)
        {
            // More than one control unit can answer (for example engine and transmission). Combine their lists,
            // so it does not matter which one replies first.
            var data = SupportedBitmap(block);
            if (data is null)
                break;

            bool nextBlock = false;
            for (int i = 0; i < 32; i++)
            {
                bool bit = (data[i / 8] & (0x80 >> (i % 8))) != 0;
                if (!bit) continue;

                if (i == 31) nextBlock = true; // PID block+0x20 means "next block is available"
                _supported.Add((byte)(block + i + 1));
            }

            if (!nextBlock) break;
            block += 0x20;
        }

        // The VIN and identity are optional: a slow or missing answer must not stop the connection.
        try { Vin = ReadVin(); }
        catch (TimeoutException) { Vin = null; }

        try { Identity = ReadIdentity(); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { /* identity is optional */ }
    }

    private EcuIdentity ReadIdentity()
    {
        var info = new List<byte>();
        var mask = ExtractPayload("0900", 0x49, 0x00, 4);
        if (mask is not null)
        {
            for (int i = 0; i < 32; i++)
                if ((mask[i / 8] & (0x80 >> (i % 8))) != 0) info.Add((byte)(i + 1));
        }

        var calibration = ReadInfoChunks("0904", 0x04, 16).Select(AsText).Where(t => t.Length > 0).ToList();
        var cvn = ReadInfoChunks("0906", 0x06, 4).Select(c => Convert.ToHexString(c)).ToList();
        var name = ReadInfoChunks("090A", 0x0A, 20).Select(AsText).FirstOrDefault(t => t.Length > 0);

        return new EcuIdentity(
            calibration, cvn, name, info,
            AdapterId,
            AtLine("AT@1"),
            AtLine("AT@2"),
            AtLine("ATRV"));
    }

    private string AtLine(string command)
    {
        var response = _elm.SendAt(command);
        return response.HasError || response.Lines.Count == 0 ? "Not reported" : response.Lines[0];
    }

    /// <summary>Mode 09 reply: "49 PID count" then <paramref name="count"/> entries of <paramref name="size"/> bytes.</summary>
    private List<byte[]> ReadInfoChunks(string request, byte pid, int size)
    {
        var chunks = new List<byte[]>();
        var response = _elm.SendObd(request, TimeSpan.FromSeconds(6));
        if (response.HasError) return chunks;

        string all = string.Concat(response.HexLines);
        int header = all.IndexOf($"49{pid:X2}", StringComparison.Ordinal);
        if (header < 0 || all.Length < header + 6) return chunks;

        var data = FromHex(all[(header + 6)..]);
        for (int i = 0; i + size <= data.Length; i += size)
            chunks.Add(data[i..(i + size)]);
        return chunks;
    }

    private static string AsText(byte[] bytes) =>
        new string(bytes.Where(b => b >= 0x20 && b < 0x7F).Select(b => (char)b).ToArray()).Trim();

    /// <summary>
    /// How long to wait for a live reading. The adapter itself answers NO DATA within a fraction of a second when
    /// the car does not reply, so a longer silence means the adapter or the link has stopped; waiting less
    /// lets a dropped connection be noticed (and reconnected) sooner.
    /// </summary>
    public static readonly TimeSpan LiveReadTimeout = TimeSpan.FromSeconds(1.5);

    public double? ReadPid(PidDefinition pid)
    {
        var data = ExtractPayload(pid.PidHex, 0x41, pid.Pid, pid.ByteCount, LiveReadTimeout);
        return data is null ? null : pid.Decode(data);
    }

    public IReadOnlyList<DtcInfo> ReadStoredDtcs() => ReadDtcs("03", 0x43, DtcStatus.Stored);

    public IReadOnlyList<DtcInfo> ReadPendingDtcs() => ReadDtcs("07", 0x47, DtcStatus.Pending);

    public IReadOnlyList<DtcInfo> ReadPermanentDtcs() => ReadDtcs("0A", 0x4A, DtcStatus.Permanent);

    private static readonly byte[] FreezePids = { 0x04, 0x05, 0x06, 0x07, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x11 };

    /// <summary>Reads the freeze frame (Mode 02, frame 0). Returns null when the ECU has none stored.</summary>
    public FreezeFrameData? ReadFreezeFrame()
    {
        var dtc = ExtractFrozen(0x02, 2);
        if (dtc is null || (dtc[0] == 0 && dtc[1] == 0)) return null;

        var values = new List<FreezeValue>();
        foreach (var pid in FreezePids)
        {
            var def = PidCatalog.Find(pid);
            if (def is null || (_supported.Count > 0 && !_supported.Contains(pid))) continue;

            var data = ExtractFrozen(pid, def.ByteCount);
            if (data is not null)
                values.Add(new FreezeValue(def.Name, def.Unit, def.Decode(data), def.Format));
        }
        return new FreezeFrameData(DtcCatalog.Format(dtc[0], dtc[1]), values);
    }

    private byte[]? ExtractFrozen(byte pid, int byteCount)
    {
        var response = _elm.SendObd($"02{pid:X2}00");
        if (response.HasError) return null;

        string prefix = $"42{pid:X2}00";
        foreach (var line in response.HexLines)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var bytes = FromHex(line[prefix.Length..]);
            return bytes.Length >= byteCount ? bytes[..byteCount] : null;
        }
        return null;
    }

    public ReadinessStatus? ReadReadiness()
    {
        var data = ExtractPayload("0101", 0x41, 0x01, 4);
        return data is null ? null : ReadinessDecoder.Decode(data, DateTime.Now);
    }

    // ----- Custom parameters (user-defined, read-only) -----

    /// <summary>Reads a custom Mode 01 or Mode 22 parameter. Returns null when the ECU does not answer.</summary>
    public double? ReadCustom(CustomParameter p) =>
        string.IsNullOrEmpty(p.Header) ? ReadCustomCore(p) : _elm.Exclusive(() => ReadCustomCore(p));

    private double? ReadCustomCore(CustomParameter p)
    {
        bool setHeader = !string.IsNullOrEmpty(p.Header);
        try
        {
            if (setHeader) _elm.SendAt("ATSH" + p.Header);

            var response = _elm.SendObd(p.Service + p.Id);
            if (response.HasError) return null;

            string prefix = (Convert.ToInt32(p.Service, 16) + 0x40).ToString("X2") + p.Id;
            foreach (var line in response.HexLines)
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var bytes = FromHex(line[prefix.Length..]);
                return bytes.Length >= p.Bytes ? p.Decode(bytes[..p.Bytes]) : null;
            }
            return null;
        }
        finally
        {
            if (setHeader) _elm.SendAt("ATSH7DF");
        }
    }

    // ----- ECU discovery (read-only) -----

    private static readonly string[] IdentificationDids = { "F190", "F187", "F188", "F191", "F18A", "F194" };

    private static readonly Dictionary<string, string> DidLabels = new()
    {
        ["F190"] = "VIN", ["F187"] = "Part number", ["F188"] = "Software number",
        ["F191"] = "Hardware number", ["F18A"] = "Supplier", ["F194"] = "Software version",
    };

    /// <summary>
    /// Probes diagnostic request IDs with "tester present" (3E 00), then reads standard identification data
    /// (22 F1xx) from each ECU that answers. Nothing is written.
    /// </summary>
    public IReadOnlyList<EcuNode> ScanEcus(IEnumerable<int> requestIds, Action<int, int>? progress = null, CancellationToken ct = default) =>
        _elm.Exclusive(() => ScanEcusCore(requestIds, progress, ct)); // live polling waits until the scan is over

    private IReadOnlyList<EcuNode> ScanEcusCore(IEnumerable<int> requestIds, Action<int, int>? progress, CancellationToken ct)
    {
        var ids = requestIds.ToList();
        var found = new List<EcuNode>();

        try
        {
            _elm.SendAt("ATST19"); // short per-request wait (about 100 ms) so a full scan stays quick
            for (int i = 0; i < ids.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                int id = ids[i];
                progress?.Invoke(i + 1, ids.Count);

                _elm.SendAt($"ATSH{id:X3}");
                var probe = _elm.SendObd("3E00", TimeSpan.FromSeconds(2));
                if (probe.HasError || probe.HexLines.Count == 0) continue;

                var fields = new Dictionary<string, string>();
                foreach (var did in IdentificationDids)
                {
                    var r = _elm.SendObd("22" + did, TimeSpan.FromSeconds(2));
                    if (r.HasError) continue;

                    string prefix = "62" + did;
                    string? line = r.HexLines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
                    if (line is null) continue;

                    string text = AsText(FromHex(line[prefix.Length..]));
                    if (text.Length > 0) fields[DidLabels[did]] = text;
                }
                found.Add(new EcuNode(id, id + 8, fields));
            }
        }
        finally
        {
            try { _elm.SendAt("ATSH7DF"); _elm.SendAt("ATST32"); }
            catch (Exception ex) when (ex is TimeoutException or IOException) { }
        }
        return found;
    }

    private IReadOnlyList<DtcInfo> ReadDtcs(string request, byte responseService, DtcStatus status)
    {
        var response = _elm.SendObd(request, TimeSpan.FromSeconds(6));
        var results = new List<DtcInfo>();
        if (response.HasError) return results; // NO DATA: nothing stored

        // One message per answering control unit. Each starts with the service byte (43/47/4A); on CAN a count
        // byte follows. The rest is pairs of bytes, one code each; 00 00 is padding.
        foreach (var message in Messages(response))
        {
            int start = message.Length > 0 && message[0] == responseService ? 1 : 0;
            if (start == 1 && IsCan) start = 2;

            for (int i = start; i + 1 < message.Length; i += 2)
            {
                if (message[i] == 0 && message[i + 1] == 0) continue;
                var dtc = DtcCatalog.Create(message[i], message[i + 1], status);
                if (!results.Any(d => d.Code == dtc.Code)) // two units can report the same code
                    results.Add(dtc);
            }
        }

        return results;
    }

    /// <summary>
    /// Splits a reply into whole messages. With CAN formatting on, the adapter shows a long (multi-frame) message
    /// as a length line ("00A" = 10 bytes) followed by numbered frames, whose numbers the parser has already
    /// removed. Anything else is a single-frame message, one per line (one per answering control unit).
    /// </summary>
    internal static List<byte[]> Messages(ElmResponse response)
    {
        var messages = new List<byte[]>();
        List<byte>? current = null;
        int expected = 0;

        foreach (var line in response.HexLines)
        {
            if (line.Length <= 3) // length line of a multi-frame message
            {
                current = new List<byte>();
                expected = Convert.ToInt32(line, 16);
                messages.Add(Array.Empty<byte>()); // filled in below
                continue;
            }

            var bytes = FromHex(line);
            if (current is not null && current.Count < expected)
            {
                current.AddRange(bytes);
                messages[^1] = current.Take(expected).ToArray();
                if (current.Count >= expected) current = null;
            }
            else
            {
                current = null;
                messages.Add(bytes);
            }
        }

        messages.RemoveAll(m => m.Length == 0);
        return messages;
    }

    /// <summary>The supported-PID bitmap for one block, combined (OR) across every control unit that answers.</summary>
    private byte[]? SupportedBitmap(byte block)
    {
        var response = _elm.SendObd($"01{block:X2}");
        if (response.HasError) return null;

        byte[]? combined = null;
        foreach (var message in Messages(response))
        {
            if (message.Length < 6 || message[0] != 0x41 || message[1] != block) continue;
            combined ??= new byte[4];
            for (int i = 0; i < 4; i++) combined[i] |= message[2 + i];
        }
        return combined;
    }

    private string? ReadVin()
    {
        var response = _elm.SendObd("0902", TimeSpan.FromSeconds(6));
        string all = string.Concat(response.HexLines);

        int header = all.IndexOf("4902", StringComparison.Ordinal);
        if (header < 0) return null;

        // After "49 02" comes the message count byte, then the 17 ASCII bytes of the VIN.
        string afterHeader = all[(header + 6)..];
        var bytes = FromHex(afterHeader);
        if (bytes.Length < 17) return null;

        var vin = System.Text.Encoding.ASCII.GetString(bytes, 0, 17);
        return vin.All(c => char.IsLetterOrDigit(c)) ? vin.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Finds the reply line that starts with "41 PID" and returns the next <paramref name="byteCount"/> bytes.
    /// Returns null when the ECU reports NO DATA or the reply is too short.
    /// </summary>
    private byte[]? ExtractPayload(string requestHex, byte responseService, byte pid, int byteCount, TimeSpan? timeout = null)
    {
        var response = _elm.SendObd(requestHex, timeout);
        if (response.HasError) return null;

        string prefix = $"{responseService:X2}{pid:X2}";
        foreach (var line in response.HexLines)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var bytes = FromHex(line[prefix.Length..]);
            if (bytes.Length < byteCount) return null;
            return bytes[..byteCount];
        }
        return null;
    }

    internal static byte[] FromHex(string hex)
    {
        int length = hex.Length / 2;
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    public void Dispose() => _elm.Dispose();
}
