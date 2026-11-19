using System.Net;
using System.Net.Sockets;
using System.Text;
using Obd2.Core.Elm;
using Obd2.Core.Obd;
using Obd2.Core.Transport;

namespace Obd2.Core.Tests;

/// <summary>Fake ELM327 that answers slowly, to test timeout behaviour on a real TCP socket.</summary>
internal sealed class SlowElm : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Dictionary<string, (int DelayMs, string Reply)> _replies;

    private readonly string _greeting;

    /// <param name="greeting">Sent as soon as a client connects, as some Wi-Fi adapters do (for example "&gt;").</param>
    public SlowElm(Dictionary<string, (int DelayMs, string Reply)> replies, string greeting = "")
    {
        _replies = replies;
        _greeting = greeting;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Task.Run(Serve);
    }

    public int Port { get; }

    private void Serve()
    {
        try
        {
            using var client = _listener.AcceptTcpClient();
            using var stream = client.GetStream();
            if (_greeting.Length > 0) stream.Write(Encoding.ASCII.GetBytes(_greeting));
            var line = new StringBuilder();
            var buffer = new byte[64];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != '\r') { line.Append((char)buffer[i]); continue; }
                    var cmd = line.ToString(); line.Clear();
                    var (delay, reply) = _replies.TryGetValue(cmd, out var r) ? r : (0, "NO DATA");
                    Thread.Sleep(delay);
                    var bytes = Encoding.ASCII.GetBytes(reply + "\r\r>");
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
    }

    public void Dispose() => _listener.Stop();
}

public class DtcDetailTests
{
    [Fact]
    public void ListEntriesAreLightButFindFillsInTheDetails()
    {
        var light = DtcDatabase.Lookup("P0420")!;
        Assert.Equal("", light.Explanation);
        Assert.Null(light.Causes);

        var full = DtcDatabase.Find("p0420")!;
        Assert.Equal(light.Description, full.Description);
        Assert.NotEmpty(full.Explanation);
        Assert.NotEmpty(full.Causes!);
        Assert.NotEmpty(full.Symptoms!);
        Assert.Contains(full.Causes!, c => c.Contains("likelihood"));
    }

    [Fact]
    public void DetailsMatchTheRightCodeAtBothEndsOfTheData()
    {
        var first = DtcDatabase.WithDetails(DtcDatabase.Lookup("B0001")!);   // first record in the data
        Assert.Contains("B0001", first.Explanation);

        var misfire = DtcDatabase.WithDetails(DtcDatabase.Lookup("P0300")!);
        Assert.Contains("misfire", misfire.Explanation, StringComparison.OrdinalIgnoreCase);

        var last = DtcDatabase.All.Last(e => e.Source == "Generic SAE J2012");
        Assert.NotEmpty(DtcDatabase.WithDetails(last).Explanation);
    }

    [Fact]
    public void OpeningACodeIsQuick()
    {
        DtcDatabase.WithDetails(DtcDatabase.Lookup("P0171")!); // first use
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var code in new[] { "P0300", "P0128", "U0100", "C0035", "B0001" })
            DtcDatabase.WithDetails(DtcDatabase.Lookup(code)!);
        Assert.True(sw.ElapsedMilliseconds / 5 < 150, $"{sw.ElapsedMilliseconds / 5} ms per code");
    }
}

public class FormulaLimitTests
{
    [Fact]
    public void DeeplyNestedFormulaIsRejectedNotACrash()
    {
        Assert.Throws<FormatException>(() => FormulaEvaluator.Compile(new string('(', 150) + "A" + new string(')', 150)));
        Assert.Throws<FormatException>(() => FormulaEvaluator.Compile(new string('-', 150) + "A"));
        Assert.Throws<FormatException>(() => FormulaEvaluator.Compile("A+" + string.Join("+", Enumerable.Repeat("1", 200))));
    }

    [Fact]
    public void NormalFormulasStillWork()
    {
        Assert.Equal(1000, FormulaEvaluator.Compile("(A*256+B)/4")(new byte[] { 0x0F, 0xA0 }));
        Assert.Equal(-40, FormulaEvaluator.Compile("-(40)")(Array.Empty<byte>()));
        Assert.Equal(5, FormulaEvaluator.Compile("((((((A))))))")(new byte[] { 5 }));
    }
}

public class TransportTimingProbe
{
    [Fact]
    public void ASilentAdapterIsNoticedWithinTwoSecondsPerLiveRead()
    {
        // Live reads used to wait 4 s each, and the app gave up after 8 of them (about 45 s of frozen values).
        using var elm = new SlowElm(new() { ["010C"] = (5000, "410C1AF8") });
        var transport = new TcpElmTransport("127.0.0.1", elm.Port);
        transport.Open();
        using var obd = new ObdService(transport);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => obd.ReadPid(PidCatalog.Find(0x0C)!));
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.0, 2.5);
    }

    [Theory]
    [InlineData(">")]
    [InlineData("\r\rELM327 v1.5\r\r>")]
    public void APromptSentOnConnectIsNotTakenAsTheFirstReply(string greeting)
    {
        // Found with a stand-in adapter: the stray prompt answered ATZ, so every later reply came one command late.
        using var elm = new SlowElm(new() { ["ATZ"] = (0, "ELM327 v1.5"), ["010C"] = (0, "410C1AF8") }, greeting);
        using var transport = new TcpElmTransport("127.0.0.1", elm.Port);
        transport.Open();

        transport.WriteLine("ATZ");
        Assert.Contains("ELM327", transport.ReadUntilPrompt(TimeSpan.FromSeconds(2)));
        transport.WriteLine("010C");
        Assert.Contains("410C1AF8", transport.ReadUntilPrompt(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void WifiLinkSurvivesSeveralReceiveTimeoutsBeforeTheReply()
    {
        // The transport polls with a 200 ms socket timeout; a 900 ms reply means four timeouts first.
        using var elm = new SlowElm(new() { ["010C"] = (900, "410C1AF8") });
        using var transport = new TcpElmTransport("127.0.0.1", elm.Port);
        transport.Open();

        transport.WriteLine("010C");
        string reply = transport.ReadUntilPrompt(TimeSpan.FromSeconds(4));

        Assert.Contains("410C1AF8", reply);
    }

    [Fact]
    public void AResetConnectionIsReportedStraightAwayNotAfterTheTimeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(() =>
        {
            var c = listener.AcceptTcpClient();
            Thread.Sleep(300);
            c.Client.LingerState = new LingerOption(true, 0); // close with a reset, like an adapter losing power
            c.Close();
        });

        using var transport = new TcpElmTransport("127.0.0.1", port);
        transport.Open();
        transport.WriteLine("010C");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.Throws<IOException>(() => transport.ReadUntilPrompt(TimeSpan.FromSeconds(6)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed.TotalSeconds:0.0} s");
        server.Wait();
        listener.Stop();
    }

    [Fact]
    public void AfterATimeoutTheNextCommandGetsItsOwnReply()
    {
        using var elm = new SlowElm(new()
        {
            ["010C"] = (1500, "410C1AF8"),   // slower than the 500 ms the client waits
            ["0105"] = (0, "41057B"),
        });
        using var client = new ElmClient(new TcpElmTransport("127.0.0.1", elm.Port));
        client.Open();

        Assert.Throws<TimeoutException>(() => client.SendObd("010C", TimeSpan.FromMilliseconds(500)));

        // The late 010C reply must be thrown away, not returned as the coolant reading.
        var second = client.SendObd("0105", TimeSpan.FromSeconds(4));
        Assert.Equal("41057B", second.HexLines.FirstOrDefault());

        var third = client.SendObd("0105", TimeSpan.FromSeconds(4));
        Assert.Equal("41057B", third.HexLines.FirstOrDefault());
    }

    [Fact]
    public void AnotherThreadCannotSendInTheMiddleOfAHeaderSequence()
    {
        var transport = new ScriptedTransport();
        using var client = new ElmClient(transport);
        using var started = new ManualResetEventSlim();

        var scan = Task.Run(() => client.Exclusive(() =>
        {
            client.SendAt("ATSH7E0");
            started.Set();
            Thread.Sleep(300); // a live-data read arrives here
            client.SendObd("3E00");
            client.SendAt("ATSH7DF");
            return 0;
        }));

        started.Wait();
        var live = Task.Run(() => client.SendObd("0105"));
        Task.WaitAll(scan, live);

        Assert.Equal(new[] { "ATSH7E0", "3E00", "ATSH7DF", "0105" }, transport.Sent);
    }
}
