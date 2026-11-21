using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Obd2.Core.Elm;
using Obd2.Core.Transport;

namespace Obd2.Core.Bridge;

/// <summary>
/// The laptop half of the bridge: a small web server that passes read-only adapter commands from RAV4 Control
/// Studio on another PC to the adapter, and returns the adapter's replies exactly as received.
///
/// Safety: every command is checked against the same read-only allowlist the app uses (<see cref="ElmClient"/>),
/// here on the laptop, so the other PC can never send anything that changes the car (clearing codes included).
/// Only devices on a private or link-local network are answered, every request must carry the access code, one
/// PC at a time can use the adapter, and repeated wrong codes are slowed down.
///
/// Requests:  GET /hello  (checks the code, returns the adapter's name)
///            POST /elm   (body: one command such as 010C; header X-Timeout-Ms; returns the raw reply)
/// Every request has the header X-Bridge-Code: &lt;code&gt;.
/// </summary>
public sealed class BridgeServer : IDisposable
{
    public const string Banner = "RAV4 Bridge 1";
    internal const int MaxClients = 8;
    private static readonly TimeSpan OwnerIdle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxCommandTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<IElmTransport> _openAdapter;
    private readonly byte[] _code;
    private readonly SemaphoreSlim _slots = new(MaxClients, MaxClients);
    private readonly ConcurrentDictionary<IPAddress, (int Count, DateTime Until)> _badCodes = new();
    private readonly object _adapterGate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private ElmClient? _elm;
    private IPAddress? _owner;
    private DateTime _ownerSeen;

    public BridgeServer(Func<IElmTransport> openAdapter, string code)
    {
        _openAdapter = openAdapter;
        _code = Encoding.ASCII.GetBytes(code);
    }

    /// <summary>One line per request or event, for the bridge window.</summary>
    public event Action<string>? Log;

    public int Port { get; private set; }
    public long Requests => Interlocked.Read(ref _requests);
    private long _requests;

    public void Start(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_listener, _cts.Token));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        lock (_adapterGate) { _elm?.Dispose(); _elm = null; }
    }

    internal static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

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
                if (request is null) { await RespondAsync(stream, 400, "Bad request", timeout.Token); return; }

                var (status, body) = await Task.Run(() => Answer(from, request));
                await RespondAsync(stream, status, body, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally { _slots.Release(); }
    }

    internal sealed record Request(string Method, string Path, Dictionary<string, string> Headers, string Body);

    private (int Status, string Body) Answer(IPAddress from, Request request)
    {
        // Wrong codes: each one from the same address makes the next answer wait longer (up to 10 s).
        if (_badCodes.TryGetValue(from, out var bad) && bad.Until > DateTime.UtcNow)
            return (429, "Too many wrong codes. Wait a little and try again.");

        if (!request.Headers.TryGetValue("X-Bridge-Code", out var given) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), _code))
        {
            int count = bad.Count + 1;
            _badCodes[from] = (count, DateTime.UtcNow.AddSeconds(Math.Min(10, count)));
            Log?.Invoke($"{from}: wrong access code");
            return (403, "Wrong access code. Copy it from RAV4 Bridge on the laptop.");
        }
        _badCodes.TryRemove(from, out _);

        // One PC at a time: the adapter keeps settings (such as which ECU it talks to) between commands.
        lock (_adapterGate)
        {
            if (_owner is not null && !_owner.Equals(from) && DateTime.UtcNow - _ownerSeen < OwnerIdle)
                return (409, $"The adapter is in use by {_owner}. Try again when it has disconnected.");
            if (_owner is null || !_owner.Equals(from)) Log?.Invoke($"{from}: connected");
            _owner = from;
            _ownerSeen = DateTime.UtcNow;
        }

        return (request.Method, request.Path) switch
        {
            ("GET", "/hello") => Hello(),
            ("POST", "/elm") => Command(from, request),
            _ => (404, "Unknown request."),
        };
    }

    private (int, string) Hello()
    {
        try
        {
            lock (_adapterGate) EnsureAdapter();
            return (200, Banner + "\n" + _adapterName);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or SocketException or InvalidOperationException)
        {
            return (502, "The laptop could not reach the adapter: " + ex.Message);
        }
    }

    private string _adapterName = "";

    private void EnsureAdapter()
    {
        if (_elm is not null) return;
        var transport = _openAdapter();
        try
        {
            var elm = new ElmClient(transport);
            elm.Open();
            _elm = elm;
            _adapterName = transport.DisplayName;
            Log?.Invoke($"adapter opened: {_adapterName}");
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    private (int, string) Command(IPAddress from, Request request)
    {
        string command = request.Body.Trim();
        if (!ElmClient.IsAllowed(command))
        {
            Log?.Invoke($"{from}: REFUSED {Printable(command)} (not read-only)");
            return (403, $"'{Printable(command)}' is not on the read-only allowlist.");
        }

        var timeout = request.Headers.TryGetValue("X-Timeout-Ms", out var t) && int.TryParse(t, out int ms)
            ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 100, (int)MaxCommandTimeout.TotalMilliseconds))
            : TimeSpan.FromSeconds(4);

        lock (_adapterGate)
        {
            try
            {
                EnsureAdapter();
                string raw = _elm!.SendRaw(command, timeout);
                Interlocked.Increment(ref _requests);
                return (200, raw);
            }
            catch (TimeoutException ex)
            {
                return (504, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // The link to the adapter dropped: close it, so the next request opens it again.
                _elm?.Dispose();
                _elm = null;
                Log?.Invoke("adapter link lost: " + ex.Message);
                return (502, "The laptop lost the adapter: " + ex.Message);
            }
        }
    }

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
            headerEnd = IndexOf(buffer, length, "\r\n\r\n"u8);
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

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern) =>
        data.AsSpan(0, length).IndexOf(pattern);

    private static async Task RespondAsync(Stream stream, int status, string body, CancellationToken ct)
    {
        string reason = status switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict",
            429 => "Too Many Requests", 502 => "Bad Gateway", 504 => "Gateway Timeout", _ => "Error",
        };
        var bytes = Encoding.Latin1.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=iso-8859-1\r\nContent-Length: {bytes.Length}\r\n" +
            "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(bytes, ct);
    }
}
