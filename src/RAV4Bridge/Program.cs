using System.Net.NetworkInformation;
using System.Net.Sockets;
using Obd2.Core.Bridge;
using Obd2.Core.Transport;

// RAV4 Bridge: runs on the laptop next to the car and shares its OBD-II adapter with RAV4 Control Studio on another
// PC on the same network. Read-only: only the app's allowlisted commands are passed on. Settings come from
// bridge.json, written by RAV4 Control Studio (Settings > Laptop bridge). Optional argument: another bridge.json.

Console.Title = "RAV4 Bridge";
string path = args.Length > 0 ? args[0] : BridgeConfig.DefaultPath;
var config = BridgeConfig.Load(path);

Console.WriteLine("RAV4 Bridge - shares this laptop's OBD-II adapter with RAV4 Control Studio on another PC (read-only).");
Console.WriteLine();

if (config is null)
{
    Console.WriteLine($"No settings found at {path}.");
    Console.WriteLine("Open RAV4 Control Studio on this laptop, go to Settings > Laptop bridge, and press Save bridge settings.");
    return Wait(1);
}
var problems = config.Problems();
if (problems.Count > 0)
{
    Console.WriteLine("The bridge settings need fixing in RAV4 Control Studio (Settings > Laptop bridge):");
    foreach (var p in problems) Console.WriteLine("  - " + p);
    return Wait(1);
}

IElmTransport OpenAdapter() => config.AdapterMode switch
{
    "Serial" => new SerialElmTransport(config.SerialPort!, config.Baud),
    "Demo" => new SimulatedElmTransport(config.SimulateFaults),
    _ => new TcpElmTransport(config.AdapterHost, config.AdapterPort),
};

using var server = new BridgeServer(OpenAdapter, config.Code, config.Protocol);
server.Log += Console.WriteLine; // each line already starts with the time
try
{
    server.Start(config.ListenPort);
}
catch (SocketException ex)
{
    Console.WriteLine($"Could not listen on port {config.ListenPort}: {ex.Message}");
    Console.WriteLine("Is RAV4 Bridge already running? Otherwise choose another port in RAV4 Control Studio.");
    return Wait(1);
}

string adapter = config.AdapterMode switch
{
    "Serial" => $"{config.SerialPort} at {config.Baud} baud",
    "Demo" => "DEMO: simulated RAV4, no car needed" + (config.SimulateFaults ? " (with fault codes)" : ""),
    _ => $"Wi-Fi {config.AdapterHost}:{config.AdapterPort}",
};
Console.WriteLine($"Adapter:      {adapter}");
Console.WriteLine($"Access code:  {config.Code}");
Console.WriteLine("Connect from the other PC (OBD2 Connection > Laptop bridge) to one of:");
foreach (var ip in Addresses()) Console.WriteLine($"              {ip}  port {server.Port}");
Console.WriteLine();
Console.WriteLine("Web page for a phone, tablet or another PC on the same network (live dials, fault codes, status):");
foreach (var ip in Addresses()) Console.WriteLine($"              http://{ip}:{server.Port}/#t={config.Code}");
Console.WriteLine();
if (config.AdapterMode != "Demo")
    Console.WriteLine("Do not connect RAV4 Control Studio on this laptop to the adapter at the same time: the adapter takes one connection.");
Console.WriteLine("If Windows asks, allow RAV4 Bridge on private networks. Press Ctrl+C or close this window to stop.");
Console.WriteLine();

var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
stop.Wait();
Console.WriteLine("Stopped.");
return 0;

// This PC's addresses that other devices can reach: the real Wi-Fi or Ethernet (with a router) first. VPN and
// virtual-machine adapters are left out, since a phone on the home network cannot reach them.
static IEnumerable<string> Addresses()
{
    static bool Virtual(NetworkInterface n) =>
        new[] { "virtual", "vmware", "vbox", "hyper-v", "vethernet", "tap", "vpn", "wireguard", "wsl", "loopback" }
            .Any(w => n.Description.Contains(w, StringComparison.OrdinalIgnoreCase) || n.Name.Contains(w, StringComparison.OrdinalIgnoreCase));

    var up = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .ToList();
    var real = up.Where(n => !Virtual(n)).ToList();
    var list = (real.Count > 0 ? real : up)
        .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString())
        .Where(a => !a.StartsWith("169.254."))
        .ToList();
    return list.Count > 0 ? list : new List<string> { "this-pc" };
}

static int Wait(int code)
{
    Console.WriteLine();
    Console.WriteLine("Press Enter to close.");
    try { Console.ReadLine(); } catch (IOException) { }
    return code;
}
