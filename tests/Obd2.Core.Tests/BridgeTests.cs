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
    public async Task ThePageIsServedWithoutTheCodeButItsDataIsNot()
    {
        using var http = new HttpClient();
        var page = await http.GetAsync($"http://127.0.0.1:{_server.Port}/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("RAV4 Control Studio", await page.Content.ReadAsStringAsync());
        Assert.Contains("default-src 'none'", string.Join("", page.Headers.GetValues("Content-Security-Policy")));

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{_server.Port}/favicon.ico")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"http://127.0.0.1:{_server.Port}/api/live")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"http://127.0.0.1:{_server.Port}/api/status")).StatusCode);

        // Requests without any code (a browser asking for an icon, say) do not count as wrong guesses.
        var withCode = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_server.Port}/api/status");
        withCode.Headers.Add("X-Bridge-Code", Code);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(withCode)).StatusCode);
    }

    private async Task<System.Text.Json.JsonElement> Api(HttpClient http, string path, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, $"http://127.0.0.1:{_server.Port}{path}");
        request.Headers.Add("X-Bridge-Code", Code);
        var response = await http.SendAsync(request);
        return System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task ThePageShowsTheValuesAPcIsReadingWithoutAskingTheCarAgain()
    {
        var transport = new BridgeElmTransport("127.0.0.1", _server.Port, Code);
        transport.Open();
        using var obd = new ObdService(transport);
        obd.Initialize(6);
        double rpm = obd.ReadPid(PidCatalog.Find(0x0C)!)!.Value;
        long relayed = _server.Requests;

        using var http = new HttpClient();
        var live = await Api(http, "/api/live");
        Assert.Equal("pc", live.GetProperty("source").GetString());
        Assert.Equal("127.0.0.1", live.GetProperty("pc").GetString());
        Assert.Equal(Math.Round(rpm, 2), live.GetProperty("values").GetProperty("rpm").GetDouble());
        Assert.Equal(relayed, _server.Requests); // the page read nothing from the car itself

        // While a PC drives the adapter, the page does not read codes itself (it would disturb the PC's session).
        var codes = await Api(http, "/api/codes", HttpMethod.Post);
        Assert.Contains("is using the adapter", codes.GetProperty("message").GetString());
    }

    [Fact]
    public async Task WithNoPcTheBridgeReadsTheCarItselfForThePage()
    {
        using var http = new HttpClient();
        // The bridge reads the values one after another, so wait until the battery voltage (read fourth) is in too;
        // a slow machine (such as the CI runner) can take a few rounds.
        System.Text.Json.JsonElement live = default;
        for (int i = 0; i < 80; i++)
        {
            live = await Api(http, "/api/live");
            if (live.GetProperty("source").GetString() == "bridge" &&
                live.GetProperty("values").GetProperty("volt").ValueKind == System.Text.Json.JsonValueKind.Number) break;
            await Task.Delay(250);
        }
        Assert.Equal("bridge", live.GetProperty("source").GetString());
        Assert.InRange(live.GetProperty("values").GetProperty("volt").GetDouble(), 13, 15);

        var codes = await Api(http, "/api/codes", HttpMethod.Post);
        Assert.Equal("P0420", codes.GetProperty("stored")[0].GetProperty("code").GetString());
        Assert.True(codes.GetProperty("monitors").GetArrayLength() > 0);

        var status = await Api(http, "/api/status");
        Assert.Equal(SimulatedElmTransport.SimulatedVin, status.GetProperty("vin").GetString());
        Assert.Equal(0, status.GetProperty("refused").GetInt64());
    }

    [Fact]
    public void ConfigProblemsAreReported()
    {
        var good = new BridgeConfig();
        Assert.Empty(good.Problems());
        Assert.Equal(10, good.Code.Length);

        Assert.Empty(new BridgeConfig { AdapterMode = "Demo" }.Problems()); // the simulated car needs no adapter details
        Assert.NotEmpty(new BridgeConfig { AdapterMode = "Car" }.Problems());

        var bad = new BridgeConfig { AdapterMode = "Serial", SerialPort = null, ListenPort = 80, Code = "short" };
        Assert.Equal(3, bad.Problems().Count);
    }
}
