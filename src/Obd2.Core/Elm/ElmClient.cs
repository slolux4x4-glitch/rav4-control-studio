using System.Text.RegularExpressions;
using Obd2.Core.Obd;
using Obd2.Core.Transport;

namespace Obd2.Core.Elm;

/// <summary>Parsed reply from the adapter for one command.</summary>
public sealed record ElmResponse(
    string Command,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> HexLines,
    string? Error,
    IReadOnlyList<bool>? Continues = null)
{
    /// <summary>
    /// For each of <see cref="HexLines"/>: true when it is a later frame ("1:", "2:" …) of a multi-frame message,
    /// so it belongs to the message before it.
    /// </summary>
    public bool IsContinuation(int index) => Continues is not null && index < Continues.Count && Continues[index];

    public bool HasError => Error is not null;

    public string Summary =>
        HasError ? Error! : (Lines.Count == 0 ? "(empty)" : string.Join(" | ", Lines));
}

/// <summary>
/// Sends commands to an ELM327 and enforces a read-only allowlist (reads and "tester present" only).
/// Only AT setup commands and OBD-II service 01 (live data), 03/07 (DTCs read) and 09 02 (VIN)
/// can be sent. Anything else (for example mode 04 "clear codes", or mode 08 actuator tests)
/// is rejected before it reaches the adapter.
/// </summary>
public sealed class ElmClient : IDisposable
{
    private static readonly Regex AllowedAt =
        new(@"^AT(Z|I|DP|RV|E[01]|L[01]|S[01]|H[01]|CAF[01]|AT[012]|SP[0-9A-C]|@[12]|SH[0-9A-F]{3}|ST[0-9A-F]{2})$", RegexOptions.Compiled);

    private static readonly Regex AllowedObd =
        new(@"^(01[0-9A-F]{2}|02[0-9A-F]{2}00|03|07|0A|09(00|02|04|06|0A)|22[0-9A-F]{4}|3E00)$", RegexOptions.Compiled);

    private static readonly Regex FramePrefix = new(@"^[0-9A-F]:", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex HexOnly = new(@"^[0-9A-F]+$", RegexOptions.Compiled);

    private static readonly string[] ErrorTokens =
    {
        "NO DATA", "STOPPED", "?", "UNABLE TO CONNECT", "CAN ERROR",
        "BUS ERROR", "DATA ERROR", "FB ERROR", "BUFFER FULL", "<RX ERROR", "ERROR"
    };

    private readonly IElmTransport _transport;
    private readonly object _gate = new();

    private readonly SessionTrace? _trace;

    public ElmClient(IElmTransport transport, SessionTrace? trace = null)
    {
        _transport = transport;
        _trace = trace;
    }

    public string TransportName => _transport.DisplayName;

    /// <summary>Raised for every command sent, with the adapter's reply (for CAN monitor and logs).</summary>
    public event Action<TrafficEntry>? Traffic;

    public void Open() => _transport.Open();

    public ElmResponse SendAt(string command, TimeSpan? timeout = null)
    {
        if (!AllowedAt.IsMatch(command))
            throw new InvalidOperationException($"Adapter command '{command}' is not on the read-only allowlist.");
        return Send(command, timeout ?? TimeSpan.FromSeconds(3));
    }

    public ElmResponse SendObd(string hexRequest, TimeSpan? timeout = null)
    {
        if (!AllowedObd.IsMatch(hexRequest))
            throw new InvalidOperationException($"OBD request '{hexRequest}' is not a read-only service.");
        return Send(hexRequest, timeout ?? TimeSpan.FromSeconds(4));
    }

    /// <summary>
    /// Runs several commands as one uninterrupted sequence, for example "point at another ECU, read, point back".
    /// Other threads' commands wait until the sequence ends, so they can never be sent to the wrong ECU.
    /// </summary>
    public T Exclusive<T>(Func<T> sequence)
    {
        lock (_gate)
            return sequence();
    }

    /// <summary>How long to wait for the reply to a command that already timed out, before sending the next one.</summary>
    internal static TimeSpan LateReplyWait { get; set; } = TimeSpan.FromSeconds(2);

    // Set when a command timed out: the adapter may still send that reply, followed by its '>' prompt.
    private bool _lateReplyPending;

    // Set when waiting for a late reply also timed out, so the adapter has gone quiet. Cleared by any reply.
    private bool _adapterSilent;

    private ElmResponse Send(string command, TimeSpan timeout)
    {
        lock (_gate) // one request on the wire at a time
        {
            if (_lateReplyPending)
                DiscardLateReply();

            var clock = _trace is null ? null : System.Diagnostics.Stopwatch.StartNew();
            string raw;
            try
            {
                _transport.WriteLine(command);
                raw = _transport.ReadUntilPrompt(timeout);
            }
            catch (TimeoutException ex)
            {
                _lateReplyPending = true;
                _trace?.Timeout(command, clock!.Elapsed, PartialReply(ex.Message));
                throw;
            }
            catch (Exception ex)
            {
                _trace?.Failure(command, ex);
                throw;
            }
            _trace?.Exchange(command, raw, clock!.Elapsed);
            _adapterSilent = false;
            var response = Parse(command, raw);

            Traffic?.Invoke(new TrafficEntry(
                DateTime.Now, command, response.Summary, response.HasError));
            return response;
        }
    }

    /// <summary>
    /// Reads and throws away the reply to a command that timed out, so it is not mistaken for the answer
    /// to the next command. This only reads: it never sends anything (an empty line would make the
    /// adapter repeat its last command).
    /// </summary>
    private void DiscardLateReply()
    {
        _lateReplyPending = false;
        if (_adapterSilent) return; // already waited once and nothing came; don't slow every retry down
        try
        {
            var late = _transport.ReadUntilPrompt(LateReplyWait);
            _trace?.LateReply(late);
        }
        catch (TimeoutException)
        {
            // The reply never came; carry on with the next command.
            _adapterSilent = true;
            _trace?.Note($"no late reply within {LateReplyWait.TotalSeconds:0.#} s");
        }
    }

    // The transports put whatever arrived before the deadline in the message as: Partial reply: "...".
    private static string? PartialReply(string message)
    {
        const string marker = "Partial reply: \"";
        int i = message.IndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? null : message[(i + marker.Length)..].TrimEnd('"');
    }

    internal static ElmResponse Parse(string command, string raw)
    {
        var lines = new List<string>();
        var hex = new List<string>();
        var continuation = new List<bool>();
        string? error = null;

        foreach (var rawLine in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.Equals(command, StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("SEARCHING", StringComparison.OrdinalIgnoreCase))
                continue;

            // K-line cars wake up with "BUS INIT: ...OK" before the first reply. That is a progress message,
            // not a failure; only "BUS INIT: ...ERROR" (or similar) means the bus could not be started.
            if (line.StartsWith("BUS INIT", StringComparison.OrdinalIgnoreCase))
            {
                if (!line.EndsWith("OK", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(line);
                    error ??= line;
                }
                continue;
            }

            lines.Add(line);
            var upper = line.ToUpperInvariant();

            if (ErrorTokens.Any(token => upper.Contains(token)))
            {
                error ??= line;
                continue;
            }

            // Multi-frame replies come back as "0: 490201...", "1: ...". Drop the frame index, but remember
            // whether this line continues the message started by frame 0.
            bool continues = FramePrefix.IsMatch(upper) && upper[0] != '0';
            var compact = Whitespace.Replace(FramePrefix.Replace(upper, ""), "");
            if (compact.Length > 0 && HexOnly.IsMatch(compact))
            {
                hex.Add(compact);
                continuation.Add(continues);
            }
        }

        return new ElmResponse(command, lines, hex, error, continuation);
    }

    public void Dispose()
    {
        lock (_gate)
            _transport.Dispose();
    }
}
