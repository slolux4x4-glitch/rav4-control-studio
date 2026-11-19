using System.IO;
using Obd2.Core.Elm;
using Obd2.Core.Transport;

namespace Obd2.Core.Tests;

public class SessionTraceTests
{
    private static string Text(SessionTrace trace)
    {
        var writer = new StringWriter();
        trace.WriteTo(writer);
        return writer.ToString();
    }

    [Fact]
    public void EveryCommandAndItsExactReplyAreRecorded()
    {
        var trace = new SessionTrace();
        using var elm = new SlowElm(new() { ["ATZ"] = (0, "ELM327 v1.5"), ["010C"] = (0, "41 0C 1A F8") });
        using var client = new ElmClient(new TcpElmTransport("127.0.0.1", elm.Port), trace);
        client.Open();

        client.SendAt("ATZ");
        client.SendObd("010C");

        var text = Text(trace);
        Assert.Contains("TX  ATZ", text);
        Assert.Contains("ELM327 v1.5\\r\\r", text);   // control characters shown, not swallowed
        Assert.Contains("TX  010C", text);
        Assert.Contains("41 0C 1A F8", text);
    }

    [Fact]
    public void TimeoutsAndTheDiscardedLateReplyAreRecorded()
    {
        var saved = ElmClient.LateReplyWait;
        ElmClient.LateReplyWait = TimeSpan.FromSeconds(3);
        try
        {
            var trace = new SessionTrace();
            using var elm = new SlowElm(new() { ["0105"] = (1200, "41 05 7B"), ["010C"] = (0, "41 0C 1A F8") });
            using var client = new ElmClient(new TcpElmTransport("127.0.0.1", elm.Port), trace);
            client.Open();

            Assert.Throws<TimeoutException>(() => client.SendObd("0105", TimeSpan.FromMilliseconds(400)));
            client.SendObd("010C");

            var text = Text(trace);
            Assert.Contains("!! no prompt after", text);
            Assert.Contains("late reply discarded: 41 05 7B", text);
            Assert.Contains("41 0C 1A F8", text);
        }
        finally
        {
            ElmClient.LateReplyWait = saved;
        }
    }

    [Fact]
    public void ARefusedCommandNeverReachesTheTrace()
    {
        var trace = new SessionTrace();
        using var client = new ElmClient(new SimulatedElmTransport(), trace);
        client.Open();

        Assert.Throws<InvalidOperationException>(() => client.SendObd("04")); // clear codes: blocked before sending
        Assert.Equal(0, trace.Count);
    }

    [Fact]
    public void OnlyTheMostRecentLinesAreKept()
    {
        var trace = new SessionTrace();
        for (int i = 0; i < SessionTrace.Capacity + 25; i++)
            trace.Note($"line {i}");

        Assert.Equal(SessionTrace.Capacity, trace.Count);
        var text = Text(trace);
        Assert.Contains("oldest 25 lines were dropped", text);
        Assert.DoesNotContain("line 24\r", text);
        Assert.Contains($"line {SessionTrace.Capacity + 24}", text);
    }

    [Theory]
    [InlineData("", "(nothing)")]
    [InlineData("OK\r\r", "OK\\r\\r")]
    [InlineData("A\u0001B", "A\\x01B")]
    public void RepliesAreShownExactly(string raw, string shown) =>
        Assert.Equal(shown, SessionTrace.Visible(raw));
}
