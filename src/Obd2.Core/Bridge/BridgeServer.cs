using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Obd2.Core.Elm;
using Obd2.Core.Obd;
using Obd2.Core.Transport;

namespace Obd2.Core.Bridge;

/// <summary>
/// The laptop half of the bridge: a small web server that
///  - passes read-only adapter commands from RAV4 Control Studio on another PC to the adapter (GET /hello, POST /elm),
///    returning the adapter's replies exactly as received; and
///  - serves a web page (GET /) that any phone, tablet or PC on the network can open to watch live values, read the
///    fault codes and see the bridge's status (GET /api/live, /api/codes, /api/status).
///
/// Safety: every command is checked against the same read-only allowlist the app uses (<see cref="ElmClient"/>),
/// here on the laptop, so nothing that changes the car can be sent (clearing codes included). Only devices on a
/// private or link-local network are answered; every request except the page itself must carry the access code;
/// repeated wrong codes are slowed down; one PC at a time can drive the adapter.
///
/// Live values on the page: while a PC is connected, they are read from the replies passing through (no extra
/// requests to the car). With no PC connected, the bridge reads the dashboard values itself while the page is open.
/// </summary>
public sealed class BridgeServer : IDisposable
{
    public const string Banner = "RAV4 Bridge 1";
    internal const int MaxClients = 16;
    private static readonly TimeSpan OwnerIdle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ViewerIdle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ValueMaxAge = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxCommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The values the page shows, in the order the bridge reads them itself.</summary>
    internal static readonly (byte Pid, string Key)[] PagePids =
    {
        (0x0C, "rpm"), (0x0D, "speed"), (0x05, "coolant"), (0x42, "volt"), (0x11, "throttle"), (0x04, "load"),
    };

    private readonly Func<IElmTransport> _openAdapter;
    private readonly byte[] _code;
    private readonly int _protocol;
    private readonly SemaphoreSlim _slots = new(MaxClients, MaxClients);
    private readonly ConcurrentDictionary<IPAddress, (int Count, DateTime Until)> _badCodes = new();
    private readonly ConcurrentDictionary<byte, (double Value, DateTime At)> _values = new();
    private readonly Queue<string> _recent = new();
    private readonly object _adapterGate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private ElmClient? _elm;
    private ObdService? _obd;
    private bool _readyForOwnReads;   // the bridge has set the adapter up for its own reads
    private string _adapterName = "";
    private IPAddress? _owner;
    private DateTime _ownerSeen, _ownerSince, _viewerSeen;
    private string _lastSource = "none";
    private object? _codesCache;
    private DateTime _codesAt;
    private long _requests, _refused;

    public BridgeServer(Func<IElmTransport> openAdapter, string code, int protocol = 6)
    {
        _openAdapter = openAdapter;
        _code = Encoding.ASCII.GetBytes(code);
        _protocol = protocol;
    }

    /// <summary>One line per request or event, for the bridge window.</summary>
    public event Action<string>? Log;

    public int Port { get; private set; }
    public long Requests => Interlocked.Read(ref _requests);

    public void Start(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_listener, _cts.Token));
        _ = Task.Run(() => PageReadLoop(_cts.Token));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        lock (_adapterGate) CloseAdapter();
    }

    private void Note(string line)
    {
        line = $"{DateTime.Now:HH:mm:ss}  {line}";
        lock (_recent)
        {
            _recent.Enqueue(line);
            while (_recent.Count > 30) _recent.Dequeue();
        }
        Log?.Invoke(line);
    }

    internal static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    private bool PcActive => _owner is not null && DateTime.UtcNow - _ownerSeen < OwnerIdle;

    // ------------------------------------------------------------------ connections

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }

            if (!_slots.Wait(0)) { client.Dispose(); continue; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IsLocalNetwork(remote.Address)) return;
                var from = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, timeout.Token);
                if (request is null) { await RespondAsync(stream, 400, "Bad request", "text/plain", timeout.Token); return; }

                var (status, body, type) = await Task.Run(() => Answer(from, request));
                await RespondAsync(stream, status, body, type, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally { _slots.Release(); }
    }

    internal sealed record Request(string Method, string Path, Dictionary<string, string> Headers, string Body);

    private const string Text = "text/plain; charset=utf-8", Json = "application/json", Html = "text/html; charset=utf-8";

    private (int Status, string Body, string Type) Answer(IPAddress from, Request request)
    {
        // The page itself holds no data, so it is served without the code; it asks for the code before any data.
        if (request.Method == "GET" && request.Path is "/" or "/index.html")
            return (200, Page.Value, Html);
        if (request.Path == "/favicon.ico")
            return (404, "", Text); // browsers ask for it on their own, without the code


        if (_badCodes.TryGetValue(from, out var bad) && bad.Until > DateTime.UtcNow)
            return (429, "Too many wrong codes. Wait a little and try again.", Text);

        if (!request.Headers.TryGetValue("X-Bridge-Code", out var given) || given.Length == 0)
            return (403, "This needs the access code shown in RAV4 Bridge on the laptop.", Text); // not a guess

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), _code))
        {
            int count = bad.Count + 1;
            _badCodes[from] = (count, DateTime.UtcNow.AddSeconds(Math.Min(10, count)));
            Note($"{from}: wrong access code");
            return (403, "Wrong access code. Copy it from RAV4 Bridge on the laptop.", Text);
        }
        _badCodes.TryRemove(from, out _);

        switch (request.Method, request.Path)
        {
            case ("GET", "/api/live"): _viewerSeen = DateTime.UtcNow; return (200, LiveJson(), Json);
            case ("GET", "/api/status"): return (200, StatusJson(), Json);
            case ("POST", "/api/codes"): return CodesJson();
        }

        // Adapter commands from a PC. One PC at a time: the adapter keeps settings (such as the ECU it talks to)
        // between commands.
        lock (_adapterGate)
        {
            if (PcActive && !_owner!.Equals(from))
                return (409, $"The adapter is in use by {_owner}. Try again when it has disconnected.", Text);
            if (!PcActive || !_owner!.Equals(from)) { Note($"{from}: PC connected"); _ownerSince = DateTime.UtcNow; }
            _owner = from;
            _ownerSeen = DateTime.UtcNow;
        }

        return (request.Method, request.Path) switch
        {
            ("GET", "/hello") => Hello(),
            ("POST", "/elm") => Command(from, request),
            _ => (404, "Unknown request.", Text),
        };
    }

    // ------------------------------------------------------------------ adapter

    private void EnsureAdapter()
    {
        if (_elm is not null) return;
        var transport = _openAdapter();
        try
        {
            var elm = new ElmClient(transport);
            elm.Open();
            _elm = elm;
            _obd = new ObdService(elm);
            _readyForOwnReads = false;
            _adapterName = transport.DisplayName;
            Note($"adapter opened: {_adapterName}");
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    private void CloseAdapter()
    {
        _elm?.Dispose();
        _elm = null;
        _obd = null;
        _readyForOwnReads = false;
    }

    private (int, string, string) Hello()
    {
        try
        {
            lock (_adapterGate) EnsureAdapter();
            return (200, Banner + "\n" + _adapterName, Text);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or SocketException or InvalidOperationException)
        {
            return (502, "The laptop could not reach the adapter: " + ex.Message, Text);
        }
    }

    private (int, string, string) Command(IPAddress from, Request request)
    {
        string command = request.Body.Trim();
        if (!ElmClient.IsAllowed(command))
        {
            Interlocked.Increment(ref _refused);
            Note($"{from}: REFUSED {Printable(command)} (not read-only)");
            return (403, $"'{Printable(command)}' is not on the read-only allowlist.", Text);
        }

        var timeout = request.Headers.TryGetValue("X-Timeout-Ms", out var t) && int.TryParse(t, out int ms)
            ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 100, (int)MaxCommandTimeout.TotalMilliseconds))
            : TimeSpan.FromSeconds(4);

        lock (_adapterGate)
        {
            try
            {
                EnsureAdapter();
                _readyForOwnReads = false; // the PC may change adapter settings; set up again before reading ourselves
                string raw = _elm!.SendRaw(command, timeout);
                Interlocked.Increment(ref _requests);
                Observe(command, raw);
                return (200, raw, Text);
            }
            catch (TimeoutException ex)
            {
                return (504, ex.Message, Text);
            }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or ObjectDisposedException)
            {
                CloseAdapter(); // the next request opens the link again
                Note("adapter link lost: " + ex.Message);
                return (502, "The laptop lost the adapter: " + ex.Message, Text);
            }
        }
    }

    /// <summary>Keeps the live values a PC reads (01 0C, 01 0D …), so the page shows them without asking the car again.</summary>
    internal void Observe(string command, string raw)
    {
        if (command.Length != 4 || !command.StartsWith("01", StringComparison.Ordinal)) return;
        if (!byte.TryParse(command.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out byte pid)) return;
        if (!PagePids.Any(p => p.Pid == pid) || PidCatalog.Find(pid) is not { } def) return;

        string prefix = $"41{pid:X2}";
        foreach (var line in ElmClient.Parse(command, raw).HexLines)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var bytes = Convert.FromHexString(line[prefix.Length..(prefix.Length + (line.Length - prefix.Length) / 2 * 2)]);
            if (bytes.Length < def.ByteCount) return;
            _values[pid] = (def.Decode(bytes), DateTime.UtcNow);
            _lastSource = "pc";
            return;
        }
    }

    /// <summary>Sets the adapter up for the bridge's own reads (after it opened, or after a PC used it).</summary>
    private void PrepareOwnReads()
    {
        EnsureAdapter();
        if (_readyForOwnReads) return;
        _obd!.Initialize(_protocol);
        _obd.LoadEcuInfo();
        _readyForOwnReads = true;
        Note($"bridge reading the car itself ({_obd.Protocol})");
    }

    /// <summary>While the page is open and no PC is connected, reads the dashboard values about twice a second.</summary>
    private async Task PageReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(400, ct); } catch (OperationCanceledException) { return; }
            if (DateTime.UtcNow - _viewerSeen > ViewerIdle || PcActive) continue;

            lock (_adapterGate)
            {
                if (PcActive) continue;
                try
                {
                    PrepareOwnReads();
                    foreach (var (pid, _) in PagePids)
                    {
                        if (_obd!.SupportedPids.Count > 0 && !_obd.SupportedPids.Contains(pid)) continue;
                        if (_obd.ReadPid(PidCatalog.Find(pid)!) is double v)
                        {
                            _values[pid] = (v, DateTime.UtcNow);
                            _lastSource = "bridge";
                        }
                    }
                }
                catch (TimeoutException) { /* car not answering this round */ }
                catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
                {
                    CloseAdapter();
                    Note("adapter not reachable: " + ex.Message);
                    try { Task.Delay(3000, ct).Wait(ct); } catch (OperationCanceledException) { return; }
                }
            }
        }
    }

    // ------------------------------------------------------------------ page data

    private string LiveJson()
    {
        var now = DateTime.UtcNow;
        var values = PagePids.ToDictionary(p => p.Key, p =>
            _values.TryGetValue(p.Pid, out var v) && now - v.At < ValueMaxAge ? (double?)Math.Round(v.Value, 2) : null);
        bool any = values.Values.Any(v => v is not null);
        return JsonSerializer.Serialize(new
        {
            source = any ? _lastSource : "none",
            demo = _adapterName.StartsWith("DEMO", StringComparison.Ordinal),
            pc = PcActive ? _owner!.ToString() : null,
            adapterOpen = _elm is not null,
            values,
        });
    }

    private string StatusJson()
    {
        string[] log;
        lock (_recent) log = _recent.Reverse().ToArray();
        return JsonSerializer.Serialize(new
        {
            adapter = _adapterName.Length > 0 ? _adapterName : "Not opened yet",
            adapterOpen = _elm is not null,
            protocol = _obd?.Protocol is { Length: > 0 } p ? p : null,
            vin = _obd?.Vin,
            pc = PcActive ? _owner!.ToString() : null,
            pcSince = PcActive ? _ownerSince.ToLocalTime().ToString("HH:mm:ss") : null,
            relayed = Interlocked.Read(ref _requests),
            refused = Interlocked.Read(ref _refused),
            log,
        });
    }

    /// <summary>Reads stored, pending and permanent codes and readiness. Only while no PC is using the adapter.</summary>
    private (int, string, string) CodesJson()
    {
        if (PcActive)
            return (409, JsonSerializer.Serialize(new { message = $"The PC at {_owner} is using the adapter. Its Diagnostics page shows the codes." }), Json);

        lock (_adapterGate)
        {
            if (_codesCache is not null && DateTime.UtcNow - _codesAt < TimeSpan.FromSeconds(10))
                return (200, JsonSerializer.Serialize(_codesCache), Json);
            try
            {
                PrepareOwnReads();
                var obd = _obd!;
                object Codes(IReadOnlyList<DtcInfo> list) => list.Select(d => new { code = d.Code, description = d.Description }).ToList();
                var readiness = obd.ReadReadiness();
                _codesCache = new
                {
                    readAt = DateTime.Now.ToString("HH:mm:ss"),
                    stored = Codes(obd.ReadStoredDtcs()),
                    pending = Codes(obd.ReadPendingDtcs()),
                    permanent = Codes(SafePermanent(obd)),
                    mil = readiness?.MilOn,
                    monitors = readiness?.Monitors.Where(m => m.Supported).Select(m => new { name = m.Name, ready = m.Complete }).ToList(),
                };
                _codesAt = DateTime.UtcNow;
                Note("codes read for the web page");
                return (200, JsonSerializer.Serialize(_codesCache), Json);
            }
            catch (TimeoutException)
            {
                return (504, JsonSerializer.Serialize(new { message = "The car did not answer. Is the ignition on?" }), Json);
            }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
            {
                CloseAdapter();
                return (502, JsonSerializer.Serialize(new { message = "The laptop could not reach the adapter: " + ex.Message }), Json);
            }
        }
    }

    private static IReadOnlyList<DtcInfo> SafePermanent(ObdService o)
    {
        try { return o.ReadPermanentDtcs(); }
        catch (TimeoutException) { return Array.Empty<DtcInfo>(); }
    }

    private static readonly Lazy<string> Page = new(() =>
    {
        using var stream = typeof(BridgeServer).Assembly.GetManifestResourceStream("bridge.html")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    // ------------------------------------------------------------------ HTTP

    private static string Printable(string s) =>
        new string(s.Take(40).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());

    internal static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int length = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (length == buffer.Length) return null; // headers too long
            int read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) return null;
            length += read;
            headerEnd = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        int bodyLength = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out int n) ? n : 0;
        if (bodyLength is < 0 or > 64) return null; // one short command per request
        int bodyStart = headerEnd + 4;
        while (length - bodyStart < bodyLength)
        {
            if (length == buffer.Length) return null;
            int read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) return null;
            length += read;
        }
        string body = Encoding.ASCII.GetString(buffer, bodyStart, bodyLength);
        return new Request(first[0], first[1].Split('?')[0], headers, body);
    }

    private static async Task RespondAsync(Stream stream, int status, string body, string type, CancellationToken ct)
    {
        string reason = status switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict",
            429 => "Too Many Requests", 502 => "Bad Gateway", 504 => "Gateway Timeout", _ => "Error",
        };
        // Adapter replies are bytes as received, so plain-text answers to /elm keep their exact characters (Latin-1).
        var bytes = type == Text && status == 200 ? Encoding.Latin1.GetBytes(body) : Encoding.UTF8.GetBytes(body);
        string charsetType = type == Text && status == 200 ? "text/plain; charset=iso-8859-1" : type;
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {charsetType}\r\nContent-Length: {bytes.Length}\r\n" +
            "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer\r\n" +
            "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(bytes, ct);
    }
}
