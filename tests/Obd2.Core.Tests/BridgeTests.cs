using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Obd2.Core.Bridge;
using Obd2.Core.Obd;
using Obd2.Core.Transport;

namespace Obd2.Core.Tests;

/// <summary>The app on one PC, RAV4 Bridge on the "laptop" (here: the same PC), and the simulator as the car.</summary>
public sealed class BridgeTests : IDisposable
{
    private const string Code = "testcode42";
    private readonly BridgeServer _server;

    public BridgeTests()
    {
        _server = new BridgeServer(() => new SimulatedElmTransport(simulateFaults: true), Code);
        _server.Start(0); // any free port
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public void TheAppWorksThroughTheBridgeAsIfTheAdapterWereLocal()
    {
        var transport = new BridgeElmTransport("127.0.0.1", _server.Port, Code);
        transport.Open(); // as the app does before talking to the adapter
        using var obd = new ObdService(transport);
        obd.Initialize(6);
        obd.LoadEcuInfo();

        Assert.True(obd.IsCan);
        Assert.Equal(SimulatedElmTransport.SimulatedVin, obd.Vin);
        Assert.InRange(obd.ReadPid(PidCatalog.Find(0x0C)!) ?? -1, 500, 4000);
        Assert.Equal("P0420", Assert.Single(obd.ReadStoredDtcs()).Code);
        Assert.Contains("Laptop bridge", obd.TransportName);
    }

    [Fact]
    public void AWrongCodeIsRefusedAndRepeatedTriesAreSlowedDown()
    {
        var wrong = new BridgeElmTransport("127.0.0.1", _server.Port, "wrongcode1");
        var ex = Assert.Throws<IOException>(wrong.Open);
        Assert.Contains("Wrong access code", ex.Message);

        var again = new BridgeElmTransport("127.0.0.1", _server.Port, Code);
        var ex2 = Assert.Throws<IOException>(again.Open); // even the right code waits after a wrong one
        Assert.Contains("Too many wrong codes", ex2.Message);
    }

    [Theory]
    [InlineData("04")]            // clear codes
    [InlineData("0100\r04")]      // a second command hidden after the first
    [InlineData("ATMA")]          // monitor all: floods the link
    [InlineData("2E F190 00")]    // UDS write data
    [InlineData("3101FF00")]      // UDS routine control
    public async Task CommandsThatAreNotReadOnlyAreRefusedOnTheLaptop(string command)
    {
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_server.Port}/elm") { Content = new StringContent(command) };
        request.Headers.Add("X-Bridge-Code", Code);
        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("allowlist", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _server.Requests);
    }

    [Fact]
    public void OnlyOnePcAtATimeCanUseTheAdapter()
    {
        using var first = new BridgeElmTransport("127.0.0.1", _server.Port, Code);
        first.Open();

        // A second PC (another address on the loopback network) is turned away while the first is active.
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
                await socket.ConnectAsync(context.DnsEndPoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        using var second = new BridgeElmTransport("127.0.0.1", _server.Port, Code, handler);
        var ex = Assert.Throws<IOException>(second.Open);
        Assert.Contains("in use by 127.0.0.1", ex.Message);
    }

    [Fact]
    public async Task RequestsWithoutTheCodeOrWithOversizedBodiesAreRejected()
    {
        using var http = new HttpClient();
        var noCode = await http.GetAsync($"http://127.0.0.1:{_server.Port}/hello");
        Assert.Equal(HttpStatusCode.Forbidden, noCode.StatusCode);

        var big = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_server.Port}/elm") { Content = new StringContent(new string('0', 500)) };
        big.Headers.Add("X-Bridge-Code", Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(big)).StatusCode);
    }

    [Fact]
    public void ConfigProblemsAreReported()
    {
        var good = new BridgeConfig();
        Assert.Empty(good.Problems());
        Assert.Equal(10, good.Code.Length);

        var bad = new BridgeConfig { AdapterMode = "Serial", SerialPort = null, ListenPort = 80, Code = "short" };
        Assert.Equal(3, bad.Problems().Count);
    }
}
