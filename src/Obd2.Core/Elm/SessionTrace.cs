using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Obd2.Core.Elm;

/// <summary>
/// Records every command sent to the adapter and its exact reply, with timing, timeouts and errors, so a
/// problem seen in the car can be investigated afterwards from one file. Thread-safe. Keeps the most recent
/// <see cref="Capacity"/> lines (about half an hour of live polling), dropping the oldest.
/// </summary>
public sealed class SessionTrace
{
    public const int Capacity = 40_000;

    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();
    private long _dropped;

    public int Count { get { lock (_gate) return _lines.Count; } }

    /// <summary>A free-text event: connecting, connection lost, reconnect attempt and so on.</summary>
    public void Note(string text) => Add($"--  {text}");

    public void Exchange(string command, string rawReply, TimeSpan elapsed) =>
        Add($"TX  {command}\r\n    RX ({elapsed.TotalMilliseconds:0} ms) {Visible(rawReply)}");

    public void Timeout(string command, TimeSpan elapsed, string? partial) =>
        Add($"TX  {command}\r\n    !! no prompt after {elapsed.TotalMilliseconds:0} ms" + (string.IsNullOrEmpty(partial) ? "" : $", partial: {Visible(partial)}"));

    public void Failure(string command, Exception ex) =>
        Add($"TX  {command}\r\n    !! {ex.GetType().Name}: " +
            (ex.Message.Length > MaxRecordedReply ? ex.Message[..MaxRecordedReply] + " ..." : ex.Message));

    public void LateReply(string rawReply) => Add($"    late reply discarded: {Visible(rawReply)}");

    /// <summary>Writes the recorded lines, oldest first.</summary>
    public void WriteTo(TextWriter writer)
    {
        string[] lines;
        long dropped;
        lock (_gate)
        {
            lines = _lines.ToArray();
            dropped = _dropped;
        }
        if (dropped > 0)
            writer.WriteLine($"(the oldest {dropped:N0} lines were dropped to keep the trace to {Capacity:N0} lines)");
        foreach (var line in lines)
            writer.WriteLine(line);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _dropped = 0;
        }
    }

    private void Add(string text)
    {
        var line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + text;
        lock (_gate)
        {
            _lines.Enqueue(line);
            if (_lines.Count > Capacity)
            {
                _lines.Dequeue();
                _dropped++;
            }
        }
    }

    /// <summary>
    /// Most characters of one reply kept in the trace. Real replies are a few hundred characters; without a limit,
    /// an adapter (or another device on its Wi-Fi) sending replies of tens of KB could make the 40,000-line trace
    /// take gigabytes of memory.
    /// </summary>
    public const int MaxRecordedReply = 2_000;

    /// <summary>Shows control characters so the reply can be read exactly as the adapter sent it.</summary>
    internal static string Visible(string raw)
    {
        int extra = raw.Length - MaxRecordedReply;
        if (extra > 0) raw = raw[..MaxRecordedReply];

        var sb = new StringBuilder(raw.Length + 40);
        foreach (char c in raw)
        {
            sb.Append(c switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when c < ' ' || c > '~' => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        }
        if (extra > 0) sb.Append($" ... ({extra:N0} more characters not kept)");
        return sb.Length == 0 ? "(nothing)" : sb.ToString();
    }
}
