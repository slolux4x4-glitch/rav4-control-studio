using Obd2.Core.Elm;
using Obd2.Core.Obd;

namespace Obd2.Core.Tests;

/// <summary>
/// Clearing codes is written but switched off. These tests run in one class (so one at a time) because some
/// briefly switch it on, against fake adapters only, and always switch it off again.
/// </summary>
public class ClearCodesTests
{
    private static ObdService Car(ScriptedTransport t, string rpmReply = "410C0000\r")
    {
        t.Replies["ATZ"] = "ELM327 v1.5\r";
        foreach (var at in new[] { "ATE0", "ATL0", "ATS0", "ATH0", "ATCAF1", "ATSP6" }) t.Replies[at] = "OK\r";
        t.Replies["ATDP"] = "ISO 15765-4 (CAN 11/500)\r";
        t.Replies["010C"] = rpmReply;
        var obd = new ObdService(t);
        obd.Initialize(6);
        return obd;
    }

    private static void WithClearingOn(Action test)
    {
        ElmClient.ClearCodesEnabled = true;
        try { test(); }
        finally { ElmClient.ClearCodesEnabled = false; }
    }

    [Fact]
    public void SwitchedOffAsShipped()
    {
        Assert.False(ElmClient.ClearCodesEnabled);
    }

    [Fact]
    public void WhileSwitchedOffNothingIsSent()
    {
        var t = new ScriptedTransport();
        using var obd = Car(t);
        t.Replies["04"] = "44\r";

        Assert.Throws<InvalidOperationException>(() => obd.ClearCodes());
        Assert.DoesNotContain("04", t.Sent);
    }

    [Fact]
    public void TheNormalReadPathStillRefusesClearingEvenWhenSwitchedOn()
    {
        var t = new ScriptedTransport();
        using var client = new ElmClient(t);
        WithClearingOn(() => Assert.Throws<InvalidOperationException>(() => client.SendObd("04")));
        Assert.DoesNotContain("04", t.Sent);
    }

    [Fact]
    public void RefusesWhileTheEngineIsRunning()
    {
        var t = new ScriptedTransport();
        using var obd = Car(t, rpmReply: "410C0BB8\r"); // 750 rpm
        t.Replies["03"] = "43010420\r";

        WithClearingOn(() =>
        {
            var result = obd.ClearCodes();
            Assert.False(result.Cleared);
            Assert.Contains("engine is running", result.Message);
        });
        Assert.DoesNotContain("04", t.Sent);
    }

    [Fact]
    public void ClearsAndConfirmsByReadingTheCodesAgain()
    {
        var t = new ScriptedTransport();
        using var obd = Car(t);
        t.Replies["04"] = "44\r";
        t.Replies["03"] = "4300\r";

        WithClearingOn(() =>
        {
            var result = obd.ClearCodes();
            Assert.True(result.Cleared);
            Assert.Empty(result.Remaining);
        });
        Assert.Equal("03", t.Sent[^1]); // read again after clearing
    }

    [Fact]
    public void ReportsWhenTheEcuRefuses()
    {
        var t = new ScriptedTransport();
        using var obd = Car(t);
        t.Replies["04"] = "7F0422\r"; // negative response: conditions not correct
        t.Replies["03"] = "43010420\r";

        WithClearingOn(() =>
        {
            var result = obd.ClearCodes();
            Assert.False(result.Cleared);
            Assert.Equal("P0420", result.Remaining.Single().Code);
        });
    }

    [Fact]
    public void ReportsWhenTheFaultIsStillPresent()
    {
        var t = new ScriptedTransport();
        using var obd = Car(t);
        t.Replies["04"] = "44\r";
        t.Replies["03"] = "43010420\r"; // code back straight away

        WithClearingOn(() =>
        {
            var result = obd.ClearCodes();
            Assert.False(result.Cleared);
            Assert.Contains("still stored", result.Message);
        });
    }
}
