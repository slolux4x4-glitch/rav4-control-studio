using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RAV4ControlStudio.Services;

/// <summary>
/// Tiny read-only web server so a phone on the same network can watch live data. It serves one page and one
/// JSON snapshot, protected by a random token in the address. It never accepts commands.
/// </summary>
public sealed class RemoteViewServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public Func<string> Snapshot { get; set; } = () => "{}";

    /// <summary>
    /// Access code in the address. Ten characters from a secure generator (about 50 bits), using letters and
    /// digits that are hard to mix up when typed on a phone.
    /// </summary>
    public string Token { get; } = RandomNumberGenerator.GetString("abcdefghjkmnpqrstuvwxyz23456789", 10);

    /// <summary>
    /// Only devices on a home or hotspot network (private, link-local or this PC) may connect, so the page is
    /// never offered to the wider internet even if the laptop has a public address.
    /// </summary>
    internal static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>True when the request's query has t=&lt;token&gt; exactly, compared in constant time.</summary>
    internal static bool HasToken(string path, string token)
    {
        int q = path.IndexOf('?');
        if (q < 0) return false;
        foreach (var pair in path[(q + 1)..].Split('&'))
        {
            if (!pair.StartsWith("t=", StringComparison.Ordinal)) continue;
            var given = Encoding.ASCII.GetBytes(pair[2..]);
            return CryptographicOperations.FixedTimeEquals(given, Encoding.ASCII.GetBytes(token));
        }
        return false;
    }

    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    public bool Start(int port, out string error)
    {
        error = "";
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Port = port;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(_listener, _cts.Token));
            return true;
        }
        catch (SocketException ex)
        {
            _listener = null;
            error = ex.Message;
            return false;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// This PC's IPv4 addresses, best first: networks with a gateway (the real Wi-Fi or Ethernet a phone can join)
    /// come before virtual adapters such as VirtualBox, Hyper-V or VPNs, which a phone cannot reach.
    /// </summary>
    public static IReadOnlyList<string> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => (Props: n.GetIPProperties(), Virtual: LooksVirtual(n)))
            .OrderByDescending(n => n.Props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
            .ThenBy(n => n.Virtual)
            .SelectMany(n => n.Props.UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsLocalNetwork(a.Address))
            .Select(a => a.Address.ToString())
            .ToList();

    private static bool LooksVirtual(NetworkInterface n) =>
        new[] { "virtual", "vmware", "vbox", "hyper-v", "vethernet", "tap", "vpn", "wsl" }
            .Any(word => n.Description.Contains(word, StringComparison.OrdinalIgnoreCase)
                      || n.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => Stop();

    /// <summary>
    /// Most connections served at once. A phone asks twice a second, so a few are plenty. Connections beyond this
    /// are closed straight away, so a misbehaving device on the network cannot pile up work in the app.
    /// </summary>
    internal const int MaxClients = 16;

    /// <summary>How long a connection may take to send its request before it is closed.</summary>
    internal static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(3);

    // Never disposed: a request that is still finishing after Stop() releases its slot here.
    private readonly SemaphoreSlim _slots = new(MaxClients, MaxClients);

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                if (!_slots.Wait(0))
                {
                    client.Dispose(); // too many open at once
                    continue;
                }
                _ = HandleAsync(client);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
        }
    }

    // Fully asynchronous: a connection that opens and then sends nothing waits without holding a thread, so it
    // cannot starve the threads the app uses to poll the car.
    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IsLocalNetwork(remote.Address))
                    return; // drop connections from outside the local network without answering

                using var timeout = new CancellationTokenSource(RequestTimeout);
                using var stream = client.GetStream();
                var buffer = new byte[4096];
                int read = await stream.ReadAsync(buffer, timeout.Token);
                string request = Encoding.ASCII.GetString(buffer, 0, read);
                string line = request.Split("\r\n")[0];
                var parts = line.Split(' ');
                string path = parts.Length > 1 ? parts[1] : "/";
                bool authorised = HasToken(path, Token);

                if (parts[0] != "GET" || !authorised)
                    await RespondAsync(stream, "403 Forbidden", "text/plain", "Open the address shown in RAV4 Control Studio (it includes a code).", timeout.Token);
                else if (path.StartsWith("/data", StringComparison.Ordinal))
                    await RespondAsync(stream, "200 OK", "application/json", Snapshot(), timeout.Token);
                else
                    await RespondAsync(stream, "200 OK", "text/html; charset=utf-8", Page.Replace("{{TOKEN}}", Token), timeout.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // a phone that disconnects mid-request, or a connection that never sends one, is not an error
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// Headers that stop browsers from guessing other content types, showing the page inside another site, loading
    /// anything from elsewhere, or passing the address (with its code) on to another site.
    /// </summary>
    internal const string SecurityHeaders =
        "X-Content-Type-Options: nosniff\r\n" +
        "X-Frame-Options: DENY\r\n" +
        "Referrer-Policy: no-referrer\r\n" +
        "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; form-action 'none'; base-uri 'none'\r\n";

    private static async Task RespondAsync(NetworkStream stream, string status, string type, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\n{SecurityHeaders}Connection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(bytes, ct);
    }

    private const string Page = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>RAV4 Live</title>
<style>
body{margin:0;background:#070d14;color:#e6edf3;font-family:Segoe UI,Arial,sans-serif}
h1{font-size:18px;margin:14px 16px 4px;color:#9aa8b6;font-weight:600}
.grid{display:grid;grid-template-columns:1fr 1fr;gap:10px;padding:10px 12px}
.card{background:#0c1620;border:1px solid #28343f;border-radius:10px;padding:14px;text-align:center}
.v{font-size:44px;font-weight:600}.u{font-size:15px;color:#9aa8b6}.l{font-size:14px;color:#9aa8b6;margin-top:4px}
#s{padding:0 16px 14px;color:#9aa8b6;font-size:13px}
</style></head><body>
<h1>RAV4 Control Studio</h1>
<div class="grid">
<div class="card"><div class="v" id="speed">--</div><div class="u" id="su"></div><div class="l">Speed</div></div>
<div class="card"><div class="v" id="rpm">--</div><div class="u">rpm</div><div class="l">Engine speed</div></div>
<div class="card"><div class="v" id="cool">--</div><div class="u" id="tu"></div><div class="l">Coolant</div></div>
<div class="card"><div class="v" id="volt">--</div><div class="u">V</div><div class="l">Battery</div></div>
<div class="card"><div class="v" id="thr">--</div><div class="u">%</div><div class="l">Throttle</div></div>
<div class="card"><div class="v" id="load">--</div><div class="u">%</div><div class="l">Engine load</div></div>
</div><div id="s">Connecting...</div>
<script>
const f=(id,x)=>document.getElementById(id).textContent=x;
async function tick(){try{const r=await fetch('/data?t={{TOKEN}}');const d=await r.json();
f('speed',d.speed);f('su',d.speedUnit);f('rpm',d.rpm);f('cool',d.coolant);f('tu',d.tempUnit);f('volt',d.volt);f('thr',d.throttle);f('load',d.load);
f('s',d.connected?'Live':'Waiting for the ECU connection...')}catch(e){f('s','Lost contact with the PC')}}
setInterval(tick,500);tick();
</script></body></html>
""";
}
