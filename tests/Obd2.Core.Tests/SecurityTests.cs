using Obd2.Core.Elm;
using Obd2.Core.Transport;

namespace Obd2.Core.Tests;

/// <summary>Guards against commands slipping past the allowlist and against a rogue device flooding the link.</summary>
public class SecurityTests
{
    [Theory]
    [InlineData("0100\n")]
    [InlineData("0100\r")]
    [InlineData("03\n")]
    [InlineData("0100\n04")]
    [InlineData(" 0100")]
    public void ObdRequestsWithExtraCharactersAreRefused(string request)
    {
        using var elm = new ElmClient(new SimulatedElmTransport());
        elm.Open();
        Assert.Throws<InvalidOperationException>(() => elm.SendObd(request));
    }

    [Theory]
    [InlineData("ATZ\n")]
    [InlineData("ATSH7E0\n")]
    [InlineData("ATZ\r04")]
    public void AdapterCommandsWithExtraCharactersAreRefused(string command)
    {
        using var elm = new ElmClient(new SimulatedElmTransport());
        elm.Open();
        Assert.Throws<InvalidOperationException>(() => elm.SendAt(command));
    }

    [Fact]
    public void PlainReadsAreStillAllowed()
    {
        using var elm = new ElmClient(new SimulatedElmTransport());
        elm.Open();
        Assert.False(elm.SendAt("ATZ").HasError);
        Assert.False(elm.SendObd("010C").HasError);
    }

    [Fact]
    public void AnEndlessReplyIsCutOffInsteadOfFillingMemory()
    {
        string flood = new('4', IElmTransport.MaxReplyLength + 10_000);
        using var fake = new SlowElm(new() { ["0100"] = (0, flood) });
        using var transport = new TcpElmTransport("127.0.0.1", fake.Port);
        transport.Open();
        transport.WriteLine("0100");

        var ex = Assert.Throws<IOException>(() => transport.ReadUntilPrompt(TimeSpan.FromSeconds(5)));
        Assert.Contains("KB", ex.Message);
    }

    [Fact]
    public void TheSessionTraceKeepsOnlyTheStartOfAHugeReply()
    {
        var trace = new SessionTrace();
        trace.Exchange("0100", new string('4', 60_000), TimeSpan.FromMilliseconds(5));
        trace.Exchange("010C", "41 0C 1A F8\r\r", TimeSpan.FromMilliseconds(5));

        var text = new StringWriter();
        trace.WriteTo(text);
        string written = text.ToString();

        Assert.True(written.Length < SessionTrace.MaxRecordedReply + 500, $"trace is {written.Length:N0} characters");
        Assert.Contains("58,000 more characters not kept", written);
        Assert.Contains("41 0C 1A F8\\r\\r", written); // normal replies are kept whole
    }

    [Fact]
    public void ABadNumberFormatInAParameterFileIsReportedWhenLoading()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rav4-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "p.json"),
                """[{"name":"Good","id":"0C"},{"name":"Bad","id":"0D","format":"F2000000000"}]""");
            var (list, errors) = Obd2.Core.Obd.CustomParameter.LoadFolder(dir);

            Assert.Equal("Good", Assert.Single(list).Name);
            Assert.Contains("p.json", Assert.Single(errors));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnOversizedParameterFileIsSkippedWithAMessage()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rav4-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var big = File.Create(Path.Combine(dir, "huge.json")))
                big.SetLength(Obd2.Core.Obd.UserFiles.MaxBytes + 1);
            File.WriteAllText(Path.Combine(dir, "ok.json"), """[{"name":"Rpm","id":"0C"}]""");

            var (list, errors) = Obd2.Core.Obd.CustomParameter.LoadFolder(dir);

            Assert.Single(list);
            Assert.Contains("not read", Assert.Single(errors));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ANormalSizedReplyStillArrives()
    {
        string reply = string.Join("\r", Enumerable.Repeat("7E8 06 41 00 BE 3F A8 13", 40));
        using var fake = new SlowElm(new() { ["0100"] = (0, reply) });
        using var transport = new TcpElmTransport("127.0.0.1", fake.Port);
        transport.Open();
        transport.WriteLine("0100");

        Assert.Contains("41 00 BE 3F", transport.ReadUntilPrompt(TimeSpan.FromSeconds(5)));
    }
}
