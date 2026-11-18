using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Obd2.Core.Elm;
using Obd2.Core.Transport;

namespace RAV4ControlStudio.Services;

/// <summary>Finds ELM327 Wi-Fi adapters: tries the usual addresses plus the gateways of the networks the PC is on.</summary>
public static class AdapterFinder
{
    public static readonly (string Host, int Port)[] Known =
    {
        ("192.168.0.10", 35000), ("192.168.0.10", 23), ("192.168.4.1", 35000), ("192.168.1.5", 35000), ("192.168.0.74", 35000),
    };

    public static async Task<IReadOnlyList<(string Host, int Port)>> FindAsync()
    {
        var candidates = new List<(string Host, int Port)>(Known);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var gw in nic.GetIPProperties().GatewayAddresses)
            {
                if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string host = gw.Address.ToString();
                foreach (int port in new[] { 35000, 23 })
                    candidates.Add((host, port));
            }
        }

        var distinct = candidates.Distinct().ToList();
        var probes = distinct.Select(c => Task.Run(() => Probe(c.Host, c.Port) ? c : ((string, int)?)null)).ToList();
        var results = await Task.WhenAll(probes);
        return results.Where(r => r is not null).Select(r => r!.Value).ToList();
    }

    private static bool Probe(string host, int port)
    {
        try
        {
            using var client = new ElmClient(new TcpElmTransport(host, port, TimeSpan.FromMilliseconds(1500)));
            client.Open();
            var reply = client.SendAt("ATI", TimeSpan.FromSeconds(2));
            return reply.Lines.Any(l => l.Contains("ELM", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or SocketException or InvalidOperationException or AggregateException)
        {
            return false;
        }
    }
}
