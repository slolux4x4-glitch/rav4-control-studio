using Obd2.Core.Elm;
using Obd2.Core.Obd;
using Obd2.Core.Transport;

namespace Obd2.Core.Tests;

/// <summary>Adapter stub: replies to each command from a fixed table (ELM327-style text).</summary>
internal sealed class ScriptedTransport : IElmTransport
{
    public Dictionary<string, string> Replies { get; } = new();
    public List<string> Sent { get; } = new();
    private string _pending = "";

    public string DisplayName => "scripted";
    public void Open() { }

    public void WriteLine(string command)
    {
        Sent.Add(command);
        _pending = Replies.TryGetValue(command, out var reply) ? reply : "NO DATA\r";
    }

    public string ReadUntilPrompt(TimeSpan timeout)
    {
        var reply = _pending;
        _pending = "";
        return reply + "\r\r";
    }

    public void Dispose() { }
}

public class ElmParsingTests
{
    [Fact]
    public void RpmReplyDecodesToEngineSpeed()
    {
        var t = new ScriptedTransport();
        t.Replies["010C"] = "410C1AF8\r";
        using var obd = new ObdService(t);

        var rpm = obd.ReadPid(PidCatalog.Find(0x0C)!);

        Assert.Equal(1726.0, rpm);
    }

    [Fact]
    public void CoolantReplyDecodesToCelsius()
    {
        var t = new ScriptedTransport();
        t.Replies["0105"] = "41057B\r"; // 0x7B = 123 -> 83 °C
        using var obd = new ObdService(t);

        Assert.Equal(83.0, obd.ReadPid(PidCatalog.Find(0x05)!));
    }

    [Fact]
    public void SearchingBannerIsIgnored()
    {
        var t = new ScriptedTransport();
        t.Replies["010C"] = "SEARCHING...\r410C1AF8\r";
        using var obd = new ObdService(t);

        Assert.Equal(1726.0, obd.ReadPid(PidCatalog.Find(0x0C)!));
    }

    [Fact]
    public void NoDataReturnsNull()
    {
        var t = new ScriptedTransport();
        t.Replies["0142"] = "NO DATA\r";
        using var obd = new ObdService(t);

        Assert.Null(obd.ReadPid(PidCatalog.Find(0x42)!));
    }

    [Fact]
    public void BatteryVoltageDecodes()
    {
        var t = new ScriptedTransport();
        t.Replies["0142"] = "4142378D\r"; // 0x378D = 14221 mV
        using var obd = new ObdService(t);

        Assert.Equal(14.221, obd.ReadPid(PidCatalog.Find(0x42)!)!.Value, 3);
    }

    [Fact]
    public void SupportedPidBitmapIsParsed()
    {
        var t = new ScriptedTransport();
        t.Replies["0100"] = "4100BE3FA813\r"; // PIDs 01,03,04,05,06,07 ... and 0x20 flag
        t.Replies["0120"] = "NO DATA\r";
        using var obd = new ObdService(t);

        obd.LoadEcuInfo();

        Assert.Contains((byte)0x01, obd.SupportedPids);
        Assert.Contains((byte)0x0C, obd.SupportedPids); // byte 2 = 0x3F includes PID 0x0C
        Assert.Contains((byte)0x05, obd.SupportedPids);
        Assert.DoesNotContain((byte)0x02, obd.SupportedPids);
    }

    [Fact]
    public void MultiFrameVinIsAssembled()
    {
        var t = new ScriptedTransport();
        // "1M8GDM9AXKP042788" split over three frames, as an ELM327 prints it with CAF1 on.
        t.Replies["0902"] = "0: 490201314D3847\r1: 444D3941584B50\r2: 303432373838\r";
        using var obd = new ObdService(t);

        obd.LoadEcuInfo();

        Assert.Equal("1M8GDM9AXKP042788", obd.Vin);
    }

    [Fact]
    public void StoredAndPendingDtcsAreDecodedAndPadIgnored()
    {
        var t = new ScriptedTransport();
        t.Replies["03"] = "4301330420000000\r";        // P0133, P0420, padding
        t.Replies["07"] = "47C10040350000\r";           // U0100, C0035 (pending)
        using var obd = new ObdService(t);

        var stored = obd.ReadStoredDtcs();
        var pending = obd.ReadPendingDtcs();

        Assert.Equal(new[] { "P0133", "P0420" }, stored.Select(d => d.Code).ToArray());
        Assert.Equal(new[] { "U0100", "C0035" }, pending.Select(d => d.Code).ToArray());
        Assert.All(stored, d => Assert.Equal(DtcStatus.Stored, d.Status));
        Assert.Contains("Catalyst", DtcCatalog.Describe("P0420"));
    }

    [Fact]
    public void ReadinessSparkIgnitionDecodesMonitors()
    {
        // MIL off, 0 DTCs, spark, misfire/fuel/components available; catalyst, evap, O2, O2 heater, EGR supported.
        var status = ReadinessDecoder.Decode(new byte[] { 0x00, 0x07, 0xE5, 0x00 }, DateTime.Now);

        Assert.False(status.MilOn);
        Assert.Equal(0, status.DtcCount);
        Assert.False(status.CompressionIgnition);
        Assert.Equal(8, status.Monitors.Count(m => m.Supported));
        Assert.True(status.AllSupportedComplete);
    }

    [Fact]
    public void IncompleteMonitorIsReported()
    {
        // D bit 6 set = O2 sensor heater test not complete.
        var status = ReadinessDecoder.Decode(new byte[] { 0x81, 0x07, 0xE5, 0x40 }, DateTime.Now);

        Assert.True(status.MilOn);
        Assert.Equal(1, status.DtcCount);
        Assert.False(status.AllSupportedComplete);
        Assert.False(status.Monitors.Single(m => m.Name == "O2 sensor heater").Complete);
    }
}

public class VinTests
{
    [Fact]
    public void ValidVinPassesCheckDigitAndYearDecodes()
    {
        var info = VinDecoder.Decode("1M8GDM9AXKP042788");

        Assert.True(info.LengthValid);
        Assert.True(info.CheckDigitValid);
        Assert.Equal(2019, info.ModelYear);
    }

    [Fact]
    public void BadCheckDigitIsFlagged()
    {
        var info = VinDecoder.Decode("1M8GDM9A0KP042788");

        Assert.False(info.CheckDigitValid);
    }
}

public class SafetyTests
{
    [Theory]
    [InlineData("ATWS")]     // warm start
    [InlineData("ATPC")]     // protocol close
    [InlineData("ATD")]      // set defaults
    public void UnlistedAdapterCommandsAreRefused(string command)
    {
        using var client = new ElmClient(new ScriptedTransport());

        Assert.Throws<InvalidOperationException>(() => client.SendAt(command));
    }

    [Theory]
    [InlineData("0400")]     // mode 04: clear diagnostic information
    [InlineData("08")]       // mode 08: control of on-board systems
    [InlineData("0A02")]     // malformed mode 0A request
    [InlineData("0101X")]
    public void NonReadOnlyObdRequestsAreRefused(string request)
    {
        using var client = new ElmClient(new ScriptedTransport());

        Assert.Throws<InvalidOperationException>(() => client.SendObd(request));
    }

    [Fact]
    public void ClearingCodesIsNotPossibleThroughTheService()
    {
        var t = new ScriptedTransport();
        using var obd = new ObdService(t);

        obd.LoadEcuInfo();
        obd.ReadStoredDtcs();
        obd.ReadPendingDtcs();
        obd.ReadReadiness();

        Assert.DoesNotContain(t.Sent, c => c.StartsWith("04", StringComparison.Ordinal));
    }
}

public class SimulatorEndToEndTests
{
    [Fact]
    public void SimulatedEcuProvidesVinSupportedPidsAndLiveValues()
    {
        using var obd = new ObdService(new SimulatedElmTransport());

        obd.Initialize(6);
        obd.LoadEcuInfo();

        Assert.Equal(SimulatedElmTransport.SimulatedVin, obd.Vin);
        Assert.Contains((byte)0x0C, obd.SupportedPids);

        var rpm = obd.ReadPid(PidCatalog.Find(0x0C)!);
        Assert.NotNull(rpm);
        Assert.InRange(rpm!.Value, 600, 1000);

        var coolant = obd.ReadPid(PidCatalog.Find(0x05)!);
        Assert.NotNull(coolant);
        Assert.InRange(coolant!.Value, -40, 130);

        Assert.Empty(obd.ReadStoredDtcs());
        Assert.NotNull(obd.ReadReadiness());
        Assert.True(VinDecoder.Decode(obd.Vin!).CheckDigitValid);
    }
}

public class DtcDatabaseTests
{
    [Fact]
    public void DatabaseHasHundredsOfCodesAndFindsByCode()
    {
        Assert.True(DtcDatabase.All.Count > 300);
        Assert.Contains("Catalyst", DtcDatabase.Find("p0420")!.Description);
        Assert.Equal("Powertrain: auxiliary emission controls", DtcDatabase.Find("P0420")!.System);
    }

    [Fact]
    public void SearchMatchesWordsAndCategory()
    {
        var misfire = DtcDatabase.Search("misfire cylinder");
        Assert.Contains(misfire, e => e.Code == "P0301");
        Assert.All(DtcDatabase.Search("lost communication", 'U'), e => Assert.Equal('U', e.Category));
        Assert.Empty(DtcDatabase.Search("zzzznotacode"));
    }

    [Fact]
    public void UnknownManufacturerCodeGetsGenericMessage()
    {
        Assert.Contains("Manufacturer-specific", DtcCatalog.Describe("P1999"));
    }
}

public class EcuIdentityTests
{
    [Fact]
    public void SimulatorReportsCalibrationCvnEcuNameAndAdapterDetails()
    {
        using var obd = new ObdService(new SimulatedElmTransport());
        obd.Initialize(6);
        obd.LoadEcuInfo();

        var id = obd.Identity!;
        Assert.Equal(new[] { "F1B3D000" }, id.CalibrationIds);
        Assert.Equal(new[] { "A1B2C3D4" }, id.CalibrationVerificationNumbers);
        Assert.Equal("ECM-ENGINE", id.EcuName);
        Assert.Contains((byte)0x02, id.SupportedInfoTypes);
        Assert.Equal("SIM-ELM-0001", id.AdapterSerial);
    }

    [Fact]
    public void ModeNineInfoRequestsAreAllowedButOthersStillRefused()
    {
        using var client = new ElmClient(new ScriptedTransport());
        client.SendObd("0904");
        client.SendObd("090A");
        Assert.Throws<InvalidOperationException>(() => client.SendObd("0908x"));
        Assert.Throws<InvalidOperationException>(() => client.SendObd("0A02"));
    }
}

public class DtcImportTests
{
    [Fact]
    public void BundledDatabaseIsFullGenericSet()
    {
        Assert.True(DtcDatabase.All.Count > 9000);
        var p0420 = DtcDatabase.Find("P0420")!;
        Assert.NotEmpty(p0420.Explanation);
        Assert.NotEmpty(p0420.Causes!);
    }

    [Fact]
    public void UserJsonAndCsvFilesAreImportedAndOverrideBuiltIn()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dtc-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), "[{\"dtc\":\"P1100\",\"description\":\"BARO sensor circuit.\"}]");
            File.WriteAllText(Path.Combine(dir, "b.csv"), "code,description\nP1120,Accelerator pedal position sensor circuit\nnot-a-code,ignored\n");

            Assert.Equal(2, DtcDatabase.LoadUserCodes(dir));
            Assert.Equal("BARO sensor circuit", DtcDatabase.Find("P1100")!.Description);
            Assert.True(DtcDatabase.Find("P1120")!.IsManufacturerSpecific);
        }
        finally
        {
            DtcDatabase.LoadUserCodes(Path.Combine(dir, "none")); // clear
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>A fake ELM327 on a real TCP socket (127.0.0.1), backed by the simulator.</summary>
internal sealed class LoopbackElm : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);

    public LoopbackElm()
    {
        _listener.Start();
        Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
        Task.Run(Serve);
    }

    public int Port { get; }

    private void Serve()
    {
        try
        {
            using var client = _listener.AcceptTcpClient();
            using var stream = client.GetStream();
            var sim = new SimulatedElmTransport();
            sim.Open();

            var line = new System.Text.StringBuilder();
            var buffer = new byte[256];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != '\r') { line.Append((char)buffer[i]); continue; }

                    sim.WriteLine(line.ToString());
                    line.Clear();
                    var reply = System.Text.Encoding.ASCII.GetBytes(sim.ReadUntilPrompt(TimeSpan.FromSeconds(1)) + ">");
                    stream.Write(reply, 0, reply.Length);
                }
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or ObjectDisposedException) { }
    }

    public void Dispose() => _listener.Stop();
}

internal sealed class DeadTransport : IElmTransport
{
    public string DisplayName => "dead adapter";
    public void Open() => throw new IOException("Port COM9 does not exist.");
    public void WriteLine(string command) { }
    public string ReadUntilPrompt(TimeSpan timeout) => "";
    public void Dispose() { }
}

public class AdapterTests
{
    [Fact]
    public void WifiTransportTalksToAnElm327OverTcp()
    {
        using var server = new LoopbackElm();
        var transport = new TcpElmTransport("127.0.0.1", server.Port);
        transport.Open();
        using var obd = new ObdService(transport);

        obd.Initialize(6);
        obd.LoadEcuInfo();

        Assert.Equal(SimulatedElmTransport.SimulatedVin, obd.Vin);
        var rpm = obd.ReadPid(PidCatalog.Find(0x0C)!);
        Assert.InRange(rpm!.Value, 600, 4000);
    }

    [Fact]
    public void SelfTestPassesEveryStepOverTcp()
    {
        using var server = new LoopbackElm();

        var report = AdapterTester.Run(new TcpElmTransport("127.0.0.1", server.Port), 6);

        Assert.True(report.AllPassed, string.Join("; ", report.Steps.Select(s => $"{s.Name}={s.Result} {s.Detail}")));
        Assert.Contains(report.Steps, s => s.Name.StartsWith("Supply voltage") && s.Detail.StartsWith("14.2"));
    }

    [Fact]
    public void SelfTestReportsADeadAdapterAndSkipsTheRest()
    {
        var report = AdapterTester.Run(new DeadTransport(), 6);

        Assert.False(report.AdapterOk);
        Assert.Equal(StepResult.Failed, report.Steps[0].Result);
        Assert.Contains("COM9", report.Steps[0].Detail);
        Assert.All(report.Steps.Skip(1), s => Assert.Equal(StepResult.Skipped, s.Result));
    }

    [Fact]
    public void SelfTestSeparatesAdapterFaultFromMissingVehicle()
    {
        var t = new ScriptedTransport();
        t.Replies["ATZ"] = "ELM327 v2.1\r";
        foreach (var ok in new[] { "ATE0", "ATL0", "ATS0", "ATH0", "ATCAF1", "ATSP6" }) t.Replies[ok] = "OK\r";
        t.Replies["ATRV"] = "12.4V\r";
        t.Replies["ATDP"] = "ISO 15765-4 (CAN 11/500)\r";
        t.Replies["0100"] = "UNABLE TO CONNECT\r";

        var report = AdapterTester.Run(t, 6);

        Assert.True(report.AdapterOk);
        Assert.False(report.AllPassed);
        Assert.Equal(StepResult.Failed, report.Steps.Single(s => s.Name == "ECU answers").Result);
        Assert.Equal(StepResult.Skipped, report.Steps.Single(s => s.Name == "Live data").Result);
        Assert.Contains("ignition", report.Summary);
    }
}

public class FreezeFrameTests
{
    [Fact]
    public void FreezeFrameAndPermanentCodesAreReadFromTheSimulator()
    {
        using var obd = new ObdService(new SimulatedElmTransport(simulateFaults: true));
        obd.Initialize(6);
        obd.LoadEcuInfo();

        var ff = obd.ReadFreezeFrame();
        Assert.NotNull(ff);
        Assert.Equal("P0420", ff!.Dtc);
        Assert.Contains(ff.Values, v => v.Name == "Engine speed");
        Assert.Empty(obd.ReadPermanentDtcs());
    }

    [Fact]
    public void NoFreezeFrameWhenThereAreNoFaults()
    {
        using var obd = new ObdService(new SimulatedElmTransport());
        obd.Initialize(6);
        obd.LoadEcuInfo();

        Assert.Null(obd.ReadFreezeFrame());
    }

    [Fact]
    public void ClearingStillBlockedButFreezeFrameRequestsAllowed()
    {
        using var client = new ElmClient(new ScriptedTransport());
        client.SendObd("020C00");
        Assert.Throws<InvalidOperationException>(() => client.SendObd("04"));
        Assert.Throws<InvalidOperationException>(() => client.SendObd("020C01"));
    }
}

public class NewFeatureTests
{
    [Fact]
    public void EncoderIsTheInverseOfEveryDecoder()
    {
        foreach (var def in PidCatalog.All)
        {
            double mid = (Math.Max(def.Min, 0) + Math.Min(def.Max, 200)) / 2;
            var bytes = PidEncoder.Encode(def, mid);
            double back = def.Decode(bytes);
            double step = Math.Abs(def.Decode(PidEncoder.Encode(def, mid + 1000)) - def.Decode(PidEncoder.Encode(def, mid - 1000)));
            Assert.True(Math.Abs(back - mid) <= Math.Max(2, Math.Abs(mid) * 0.02) || step == 0,
                $"{def.Name}: wanted {mid}, got {back}");
        }
    }

    [Fact]
    public void SimulatorReportsFuelTrimsO2AndLambda()
    {
        using var obd = new ObdService(new SimulatedElmTransport());
        obd.Initialize(6);
        obd.LoadEcuInfo();

        foreach (byte pid in new byte[] { 0x08, 0x14, 0x15, 0x24, 0x3C })
            Assert.Contains(pid, obd.SupportedPids);

        double lambda = obd.ReadPid(PidCatalog.Find(0x24)!)!.Value;
        Assert.InRange(lambda, 0.9, 1.1);
        Assert.InRange(obd.ReadPid(PidCatalog.Find(0x14)!)!.Value, 0.0, 1.3);
    }

    [Fact]
    public void FormulaEvaluatorHandlesBytesPrecedenceAndErrors()
    {
        Assert.Equal(2000.0, FormulaEvaluator.Compile("(A*256+B)/4").Invoke(new byte[] { 0x1F, 0x40 }));
        Assert.Equal(-4.0, FormulaEvaluator.Compile("A - 40 * 2 + 1").Invoke(new byte[] { 75 }));
        Assert.Equal(-5.0, FormulaEvaluator.Compile("-(A)").Invoke(new byte[] { 5 }));
        Assert.Throws<FormatException>(() => FormulaEvaluator.Compile("A +"));
        Assert.Throws<FormatException>(() => FormulaEvaluator.Compile("Z * 2"));
    }

    [Fact]
    public void CustomModeTwentyTwoParameterIsReadWithItsHeader()
    {
        var t = new ScriptedTransport();
        t.Replies["ATSH7E0"] = "OK\r";
        t.Replies["ATSH7DF"] = "OK\r";
        t.Replies["2201A0"] = "6201A01F40\r";
        using var obd = new ObdService(t);

        var p = new CustomParameter("Test", "rpm", "22", "01A0", "7E0", 2, "(A*256+B)/4", 0, 8000, "0");

        Assert.Equal(2000.0, obd.ReadCustom(p));
        Assert.Contains("ATSH7E0", t.Sent);
        Assert.Equal("ATSH7DF", t.Sent[^1]);
    }

    [Fact]
    public void CustomParameterFilesAreValidatedAndWriteServicesRejected()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"),
                "[{\"name\":\"CVT temp\",\"unit\":\"C\",\"service\":\"22\",\"id\":\"2101\",\"header\":\"7E1\",\"bytes\":1,\"formula\":\"A-40\"}," +
                " {\"name\":\"Bad write\",\"service\":\"2E\",\"id\":\"F190\",\"formula\":\"A\"}]");

            var (list, errors) = CustomParameter.LoadFolder(dir);

            Assert.Single(list);
            Assert.Equal("CVT temp", list[0].Name);
            Assert.Contains(errors, e => e.Contains("read-only"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EcuScanFindsEnginePlusTwoOtherEcusAndReadsIds()
    {
        using var obd = new ObdService(new SimulatedElmTransport());
        obd.Initialize(6);

        var found = obd.ScanEcus(Enumerable.Range(0x7B0, 0x40));

        Assert.Equal(new[] { "7B0", "7E0", "7E1" }, found.Select(f => f.RequestHex).ToArray());
        var engine = found.Single(f => f.RequestHex == "7E0");
        Assert.Equal("7E8", engine.ResponseHex);
        Assert.Equal(SimulatedElmTransport.SimulatedVin, engine.Fields["VIN"]);
        Assert.Equal("89661-SIM00", engine.Fields["Part number"]);
    }

    [Fact]
    public void WritingServicesAreStillRefused()
    {
        using var client = new ElmClient(new ScriptedTransport());
        client.SendObd("3E00");
        client.SendObd("22F190");
        foreach (var bad in new[] { "2EF19000", "2701", "1003", "3101", "04", "0400" })
            Assert.Throws<InvalidOperationException>(() => client.SendObd(bad));
    }

    [Fact]
    public void ReplayTransportServesLoggedValuesThroughTheNormalStack()
    {
        string path = Path.Combine(Path.GetTempPath(), "replay-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path,
            "Timestamp,Engine speed [rpm],Coolant temperature [°C]\n" +
            "2026-01-01T10:00:00.0000000,800,85\n" +
            "2026-01-01T10:00:05.0000000,2400,86\n");
        try
        {
            var transport = new LogReplayTransport(path);
            transport.Open();
            using var obd = new ObdService(transport);
            obd.Initialize(6);
            obd.LoadEcuInfo();

            Assert.Contains((byte)0x0C, obd.SupportedPids);
            Assert.Contains((byte)0x05, obd.SupportedPids);
            Assert.DoesNotContain((byte)0x0D, obd.SupportedPids);
            Assert.Equal(800.0, obd.ReadPid(PidCatalog.Find(0x0C)!));
            Assert.Equal(85.0, obd.ReadPid(PidCatalog.Find(0x05)!));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PdfWriterProducesAStructurallyValidMultiPageFile()
    {
        var lines = Enumerable.Range(1, 160).Select(i => $"Line {i} with (brackets) and a degree sign \u00B0");
        var bytes = SimplePdf.FromText("Test report", lines);
        string text = System.Text.Encoding.Latin1.GetString(bytes);

        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("/Count 3", text);
        Assert.Contains("startxref", text);
    }
}
