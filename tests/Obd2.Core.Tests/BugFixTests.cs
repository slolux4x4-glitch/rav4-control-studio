using Obd2.Core.Elm;
using Obd2.Core.Obd;
using Obd2.Core.Transport;

namespace Obd2.Core.Tests;

/// <summary>Adapter stand-in with fixed replies per command; a reply can depend on what was sent before.</summary>
internal sealed class ScriptedElm : IElmTransport
{
    private readonly Func<string, IReadOnlyList<string>, string?> _reply;
    private readonly List<string> _sent = new();
    private string _pending = "";

    public ScriptedElm(Func<string, IReadOnlyList<string>, string?> reply) => _reply = reply;

    public ScriptedElm(Dictionary<string, string> replies) : this((cmd, _) => replies.GetValueOrDefault(cmd)) { }

    public IReadOnlyList<string> Sent => _sent;
    public string DisplayName => "scripted";
    public void Open() { }

    public void WriteLine(string command)
    {
        _pending = _reply(command, _sent) ?? (command.StartsWith("AT", StringComparison.Ordinal) ? "OK" : "NO DATA");
        _sent.Add(command);
    }

    public string ReadUntilPrompt(TimeSpan timeout) => _pending + "\r\r";
    public void Dispose() { }
}

public class BugFixTests
{
    [Fact]
    public void SupportedPidScanStopsAfterTheLastBlock()
    {
        // Every block claims "the next block is supported too", including the last one (E0).
        var elm = new ScriptedElm((cmd, _) => cmd.Length == 4 && cmd.StartsWith("01") && Convert.ToInt32(cmd[2..], 16) % 0x20 == 0
            ? $"41{cmd[2..]}FFFFFFFF"
            : null);
        using var obd = new ObdService(elm);

        var run = Task.Run(obd.LoadEcuInfo);
        Assert.True(run.Wait(TimeSpan.FromSeconds(10)), "the scan never finished");
        Assert.Equal(255, obd.SupportedPids.Count); // 01 to FF
        Assert.Equal(8, elm.Sent.Count(c => c.StartsWith("01")));
    }

    [Fact]
    public void AutomaticProtocolIsIdentifiedAfterTheFirstRequestSoCanCodesDecodeCorrectly()
    {
        var elm = new ScriptedElm((cmd, sent) => cmd switch
        {
            "ATDP" => sent.Contains("0100") ? "AUTO, ISO 15765-4 (CAN 11/500)" : "AUTO",
            "0100" => "4100BE3EA813",
            "03" => "43010420", // CAN: count byte 01, then P0420
            _ => null,
        });
        using var obd = new ObdService(elm);
        obd.Initialize(0);
        Assert.False(obd.IsCan); // the adapter cannot know yet

        obd.LoadEcuInfo();
        Assert.True(obd.IsCan);
        Assert.Contains("15765", obd.Protocol);
        Assert.Equal("P0420", Assert.Single(obd.ReadStoredDtcs()).Code);
    }

    [Fact]
    public void ACustomParameterLongerThanOneFrameIsRead()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rav4-fix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "p.json"),
                """[{"name":"Eighth byte","service":"22","id":"F190","bytes":8,"formula":"H"}]""");
            var p = Assert.Single(CustomParameter.LoadFolder(dir).Parameters);

            // 11 bytes (62 F1 90 + 8 data bytes) arrive as a first frame and one continuation frame.
            var elm = new ScriptedElm(new Dictionary<string, string> { ["22F190"] = "00B\r0:62F19001020304\r1:05060708000000" });
            using var obd = new ObdService(elm);

            Assert.Equal(8, obd.ReadCustom(p));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EcuScanReadsMultiFrameIdentificationData()
    {
        var elm = new ScriptedElm((cmd, sent) =>
        {
            bool engine = sent.LastOrDefault(s => s.StartsWith("ATSH")) == "ATSH7E0";
            if (!engine) return cmd.StartsWith("AT") ? null : "NO DATA";
            return cmd switch
            {
                "3E00" => "7E00",
                // VIN 4T1C11AK5MU123456: 3 + 17 = 20 bytes (014) over three frames
                "22F190" => "014\r0:62F190345431\r1:433131414B354D\r2:55313233343536",
                _ => null,
            };
        });
        using var obd = new ObdService(elm);

        var node = Assert.Single(obd.ScanEcus(new[] { 0x7E0, 0x7E1 }));
        Assert.Equal("4T1C11AK5MU123456", node.Fields["VIN"]);
    }

    [Theory]
    [InlineData('M', 2026, 2021)]
    [InlineData('T', 2026, 2026)]
    [InlineData('V', 2026, 2027)] // next year's models go on sale early
    [InlineData('W', 2026, 1998)]
    [InlineData('5', 2026, 2005)]
    [InlineData('A', 2026, 2010)]
    public void ModelYearCodesThatRepeatPickThePastCycle(char code, int now, int expected) =>
        Assert.Equal(expected, VinDecoder.YearFor("ABCDEFGHJKLMNPRSTVWXY123456789".IndexOf(code), now));
}
