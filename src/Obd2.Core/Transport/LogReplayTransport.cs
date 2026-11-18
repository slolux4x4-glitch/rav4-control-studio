using System.Diagnostics;
using System.Globalization;
using Obd2.Core.Obd;

namespace Obd2.Core.Transport;

/// <summary>
/// Replays a saved CSV log as if it were a live ECU. Columns are matched to standard PIDs by name
/// ("Engine speed [rpm]" and so on). Playback loops at the end of the file. Everything else (adapter commands,
/// VIN, readiness) is answered by the built-in simulator.
/// </summary>
public sealed class LogReplayTransport : IElmTransport
{
    private readonly SimulatedElmTransport _inner = new();
    private readonly Stopwatch _clock = new();
    private readonly string _path;
    private readonly List<double> _times = new();
    private readonly List<double?[]> _rows = new();
    private readonly List<PidDefinition> _columns = new();
    private double[] _last = Array.Empty<double>();
    private string? _own;

    public LogReplayTransport(string csvPath)
    {
        _path = csvPath;
        Load();
    }

    public string DisplayName => "REPLAY " + Path.GetFileName(_path);

    public double DurationSeconds => _times.Count > 0 ? _times[^1] : 0;

    public int RowCount => _rows.Count;

    public IReadOnlyList<PidDefinition> Parameters => _columns;

    public void Open() { _clock.Restart(); _inner.Open(); }

    public void WriteLine(string command)
    {
        _own = null;
        string cmd = command.Trim().ToUpperInvariant();

        if (cmd.Length == 4 && cmd.StartsWith("01", StringComparison.Ordinal) && byte.TryParse(cmd[2..], NumberStyles.HexNumber, null, out byte pid))
        {
            if (pid % 0x20 == 0) { _own = $"41{pid:X2}{Mask(pid)}\r"; return; }
            int col = _columns.FindIndex(c => c.Pid == pid);
            if (col >= 0)
            {
                var value = ValueAt(col);
                _own = value is null ? "NO DATA\r" : $"41{pid:X2}{Convert.ToHexString(PidEncoder.Encode(_columns[col], value.Value))}\r";
                return;
            }
        }
        _inner.WriteLine(command);
    }

    public string ReadUntilPrompt(TimeSpan timeout)
    {
        if (_own is not null)
        {
            Thread.Sleep(20);
            var reply = _own + "\r";
            _own = null;
            return reply;
        }
        return _inner.ReadUntilPrompt(timeout);
    }

    public void Dispose() => _inner.Dispose();

    private double? ValueAt(int col)
    {
        if (_rows.Count == 0) return null;

        double t = DurationSeconds > 0 ? _clock.Elapsed.TotalSeconds % DurationSeconds : 0;
        int idx = _times.BinarySearch(t);
        if (idx < 0) idx = Math.Max(0, ~idx - 1);

        // forward-fill: use the most recent non-empty value at or before this row
        for (int i = Math.Min(idx, _rows.Count - 1); i >= 0; i--)
            if (_rows[i][col] is double v) return v;
        return null;
    }

    private string Mask(byte block)
    {
        var supported = new HashSet<int>(_columns.Select(c => (int)c.Pid)) { 0x01 };
        uint mask = 0;
        for (int pid = block + 1; pid <= block + 0x20; pid++)
        {
            bool has = supported.Contains(pid) || (pid == block + 0x20 && supported.Any(p => p > block + 0x20));
            if (has) mask |= 1u << (32 - (pid - block));
        }
        return mask.ToString("X8");
    }

    private void Load()
    {
        var lines = File.ReadAllLines(_path);
        if (lines.Length < 2) throw new InvalidDataException("The log file has no data rows.");

        var header = lines[0].Split(',');
        var map = new List<(int Index, PidDefinition Def)>();
        for (int i = 1; i < header.Length; i++)
        {
            string name = header[i].Trim().Trim('"');
            int bracket = name.LastIndexOf(" [", StringComparison.Ordinal);
            if (bracket > 0) name = name[..bracket];
            var def = PidCatalog.All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (def is not null) map.Add((i, def));
        }
        if (map.Count == 0) throw new InvalidDataException("No known parameters were found in the log header.");
        _columns.AddRange(map.Select(m => m.Def));

        DateTime? start = null;
        for (int r = 1; r < lines.Length; r++)
        {
            var parts = lines[r].Split(',');
            if (parts.Length == 0 || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp)) continue;
            start ??= stamp;

            var values = new double?[map.Count];
            for (int c = 0; c < map.Count; c++)
            {
                int idx = map[c].Index;
                if (idx < parts.Length && double.TryParse(parts[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    values[c] = v;
            }
            _times.Add((stamp - start.Value).TotalSeconds);
            _rows.Add(values);
        }
        if (_rows.Count == 0) throw new InvalidDataException("The log file has no readable rows.");
    }
}
