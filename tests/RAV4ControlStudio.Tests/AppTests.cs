using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Obd2.Core.Elm;
using Obd2.Core.Obd;
using Obd2.Core.Transport;
using RAV4ControlStudio.Controls;
using RAV4ControlStudio.Services;
using RAV4ControlStudio.ViewModels;

namespace RAV4ControlStudio.Tests;

public class DebouncerTests
{
    [Fact]
    public void RapidRequestsRunTheActionOnceAfterTheDelay()
    {
        Sta.Run(() =>
        {
            int runs = 0;
            var debouncer = new Debouncer(TimeSpan.FromMilliseconds(150), () => runs++, Dispatcher.CurrentDispatcher);

            for (int i = 0; i < 5; i++) { debouncer.Request(); Sta.Pump(TimeSpan.FromMilliseconds(40)); } // like typing
            Assert.Equal(0, runs);
            Assert.True(debouncer.IsPending);

            Sta.Pump(TimeSpan.FromMilliseconds(400));
            Assert.Equal(1, runs);
            Assert.False(debouncer.IsPending);
        });
    }

    [Fact]
    public void FlushRunsAPendingRequestStraightAwayAndCancelDropsIt()
    {
        Sta.Run(() =>
        {
            int runs = 0;
            var debouncer = new Debouncer(TimeSpan.FromSeconds(5), () => runs++, Dispatcher.CurrentDispatcher);

            debouncer.Request();
            debouncer.Flush();
            Assert.Equal(1, runs);
            debouncer.Flush();               // nothing waiting: no second run
            Assert.Equal(1, runs);

            debouncer.Request();
            debouncer.Cancel();
            Sta.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(1, runs);
        });
    }
}

public class RemoteViewTests
{
    [Theory]
    [InlineData("/?t=abcdefghjk", true)]
    [InlineData("/data?x=1&t=abcdefghjk", true)]
    [InlineData("/?t=abcdefghjkx", false)]   // longer than the code
    [InlineData("/?t=abcdefghj", false)]     // shorter
    [InlineData("/?t=ABCDEFGHJK", false)]    // wrong case
    [InlineData("/xt=abcdefghjk", false)]    // no query
    [InlineData("/?q=abcdefghjk", false)]
    public void AccessCodeMustMatchExactly(string path, bool expected) =>
        Assert.Equal(expected, RemoteViewServer.HasToken(path, "abcdefghjk"));

    [Theory]
    [InlineData("192.168.0.20", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("2001:4860::8888", false)]
    public void OnlyLocalNetworksMayConnect(string address, bool expected) =>
        Assert.Equal(expected, RemoteViewServer.IsLocalNetwork(IPAddress.Parse(address)));

    [Fact]
    public void CodesAreLongAndRandom()
    {
        var a = new RemoteViewServer().Token;
        var b = new RemoteViewServer().Token;
        Assert.Equal(10, a.Length);
        Assert.NotEqual(a, b);
        Assert.All(a, c => Assert.Contains(c, "abcdefghjkmnpqrstuvwxyz23456789"));
    }

    [Fact]
    public async Task ServesDataOnlyWithTheRightCode()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }

        using var server = new RemoteViewServer { Snapshot = () => "{\"rpm\":\"1830\"}" };
        Assert.True(server.Start(port, out string error), error);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string baseUrl = $"http://127.0.0.1:{port}";

        var ok = await http.GetAsync($"{baseUrl}/data?t={server.Token}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("{\"rpm\":\"1830\"}", await ok.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"{baseUrl}/data?t=wrongcode1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"{baseUrl}/data")).StatusCode);

        var page = await http.GetStringAsync($"{baseUrl}/?t={server.Token}");
        Assert.Contains(server.Token, page); // the page polls /data with the same code

        server.Stop();
        Assert.False(server.IsRunning);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task ResponsesCarryBrowserSecurityHeaders()
    {
        int port = FreePort();
        using var server = new RemoteViewServer();
        Assert.True(server.Start(port, out string error), error);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var page = await http.GetAsync($"http://127.0.0.1:{port}/?t={server.Token}");

        Assert.Equal("nosniff", string.Join("", page.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", string.Join("", page.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", string.Join("", page.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("default-src 'none'", string.Join("", page.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task ConnectionsBeyondTheLimitAreClosedAndIdleOnesDoNotBlockTheApp()
    {
        int port = FreePort();
        using var server = new RemoteViewServer();
        Assert.True(server.Start(port, out string error), error);

        // Fill every slot with connections that never send a request.
        var idle = new List<TcpClient>();
        try
        {
            for (int i = 0; i < RemoteViewServer.MaxClients; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                idle.Add(c);
            }
            await Task.Delay(200);

            // One more is closed straight away rather than queued.
            using var extra = new TcpClient();
            await extra.ConnectAsync(IPAddress.Loopback, port);
            extra.ReceiveTimeout = 2000;
            int read;
            try { read = extra.GetStream().Read(new byte[16], 0, 16); }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { read = 0; }
            Assert.Equal(0, read); // closed, not left hanging (a hang would time out with an exception instead)

            // The idle connections wait without holding threads, so the thread pool still runs work promptly.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() => { });
            Assert.True(clock.ElapsedMilliseconds < 500, $"thread pool stalled for {clock.ElapsedMilliseconds} ms");
        }
        finally
        {
            foreach (var c in idle) c.Dispose();
        }

        // Once they are gone, a phone is served again.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        HttpResponseMessage? ok = null;
        for (int attempt = 0; attempt < 20 && ok?.StatusCode != HttpStatusCode.OK; attempt++)
        {
            try { ok = await http.GetAsync($"http://127.0.0.1:{port}/data?t={server.Token}"); }
            catch (HttpRequestException) { await Task.Delay(100); }
        }
        Assert.Equal(HttpStatusCode.OK, ok?.StatusCode);
    }
}

public class FileSafetyTests
{
    [Theory]
    [InlineData("Engine speed [rpm]", "Engine speed [rpm]")]
    [InlineData("=HYPERLINK(\"http://x\") [%]", "\"'=HYPERLINK(\"\"http://x\"\") [%]\"")]
    [InlineData("+1 [V]", "'+1 [V]")]
    [InlineData("-cmd [V]", "'-cmd [V]")]
    [InlineData("@SUM(A1) [V]", "'@SUM(A1) [V]")]
    [InlineData("Fuel, trim [%]", "\"Fuel, trim [%]\"")]
    [InlineData("two\nlines [x]", "\"two\nlines [x]\"")]
    public void CsvHeadersCannotBecomeSpreadsheetFormulas(string name, string expected) =>
        Assert.Equal(expected, CsvLogger.Escape(name));

    [Theory]
    [InlineData("Hilux", "Hilux")]
    [InlineData("..", "_")]
    [InlineData(".", "_")]
    [InlineData("   ", "_")]
    [InlineData("a/../b", "a_.._b")]
    [InlineData("..\\..\\Windows", ".._.._Windows")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Com1", "_Com1")]
    [InlineData("My car.", "My car")]
    [InlineData("Console", "Console")]
    public void VehicleFoldersStayInsideTheVehiclesFolder(string vehicle, string expected)
    {
        string folder = MainViewModel.VehicleFolderName(vehicle);
        Assert.Equal(expected, folder);

        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Vehicles"));
        string full = Path.GetFullPath(Path.Combine(root, folder));
        Assert.StartsWith(root + Path.DirectorySeparatorChar, full);
    }
}

public class LogReplayTests
{
    [Fact]
    public void ALogWrittenByTheAppReplaysTheSameValues()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rav4-replay-test-{Guid.NewGuid():N}.csv");
        try
        {
            var rpm = new PidValue(PidCatalog.Find(0x0C)!);
            var coolant = new PidValue(PidCatalog.Find(0x05)!);
            var speed = new PidValue(PidCatalog.Find(0x0D)!);

            using (var log = new CsvLogger(path, new[] { rpm, coolant, speed }))
            {
                var t = DateTime.Now;
                for (int i = 0; i < 20; i++)
                {
                    rpm.Value = 1800;
                    coolant.Value = 85;
                    speed.Value = i < 10 ? double.NaN : 50; // a gap, as when a reading is missing
                    log.WriteRow(t.AddMilliseconds(200 * i));
                }
                Assert.Equal(20, log.RowCount);
            }

            using var service = new ObdService(new LogReplayTransport(path));
            service.Initialize(6);
            service.LoadEcuInfo(); // as the app does on connect: reads the supported-PID list

            Assert.Contains((byte)0x0C, service.SupportedPids);
            Assert.Equal(1800, service.ReadPid(PidCatalog.Find(0x0C)!)!.Value, 0);
            Assert.Equal(85, service.ReadPid(PidCatalog.Find(0x05)!)!.Value, 0);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ModeBadgeTests
{
    [Theory]
    [InlineData(LinkState.Disconnected, ConnectionMode.Wifi, "Not Connected", "Read-only")]
    [InlineData(LinkState.Error, ConnectionMode.Serial, "Not Connected", "Read-only")]
    [InlineData(LinkState.Simulated, ConnectionMode.Demo, "Demo Mode", "Simulated ECU · Read-only")]
    [InlineData(LinkState.Connected, ConnectionMode.Wifi, "Vehicle Mode", "Wi-Fi adapter · Read-only")]
    [InlineData(LinkState.Connected, ConnectionMode.Serial, "Vehicle Mode", "Serial adapter · Read-only")]
    [InlineData(LinkState.Replay, ConnectionMode.Replay, "Log Replay", "Saved log · Read-only")]
    public void BadgeNamesWhatTheAppIsTalkingTo(LinkState state, ConnectionMode mode, string title, string detail)
    {
        var badge = MainViewModel.ModeBadge(state, mode);
        Assert.Equal(title, badge.Title);
        Assert.Equal(detail, badge.Detail);
    }

    [Fact]
    public void EveryStateSaysReadOnly()
    {
        foreach (var state in Enum.GetValues<LinkState>())
            foreach (var mode in Enum.GetValues<ConnectionMode>())
                Assert.EndsWith("Read-only", MainViewModel.ModeBadge(state, mode).Detail);
    }
}

public class ClearCodesPlaceholderTests
{
    // The Clear codes button is a placeholder for a possible future feature and must stay off until there are
    // instructions to build it. Turning it on should be a deliberate change that also updates this test.
    [Fact]
    public void ClearCodesStaysDisabled()
    {
        bool enabled = typeof(MainViewModel)
            .GetField("ClearCodesEnabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) is true;
        Assert.False(enabled);
    }

    [Fact]
    public void TheAdapterStillRefusesTheClearRequest()
    {
        using var client = new ElmClient(new SimulatedElmTransport());
        client.Open();
        Assert.Throws<InvalidOperationException>(() => client.SendObd("04"));
    }
}

public class ChartUnitTests
{
    [Fact]
    public void SwitchingUnitsRedrawsTheCoolantScale()
    {
        bool wasImperial = Units.Imperial;
        try
        {
            Sta.Run(() =>
            {
                var chart = new TimeSeriesChart
                {
                    Width = 700,
                    Height = 300,
                    Series = new[]
                    {
                        new ChartSeries("Engine speed", Brushes.DeepSkyBlue, 0, 6000, 50),
                        new ChartSeries("Coolant", Brushes.LimeGreen, 0, 120, 50),
                        new ChartSeries("Battery", Brushes.Orange, 10, 16, 50),
                    },
                    Times = new[] { 0.0, 1.0 },
                };
                chart.Resources["MutedBrush"] = Brushes.Gray;

                Units.Imperial = false;
                var metric = Render(chart);
                Assert.Equal(metric, Render(chart));         // the same units draw the same picture

                Units.Imperial = true;
                chart.Redraw();
                var imperial = Render(chart);
                Assert.NotEqual(metric, imperial);           // the cached scale was redrawn in °F
            });
        }
        finally
        {
            Units.Imperial = wasImperial;
        }
    }

    private static string Render(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)element.Width, (int)element.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[(int)element.Width * (int)element.Height * 4];
        bitmap.CopyPixels(pixels, (int)element.Width * 4, 0);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
    }
}
