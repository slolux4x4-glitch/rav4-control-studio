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
