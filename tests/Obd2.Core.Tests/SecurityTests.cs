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
