using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Obd2.Core.Transport;

namespace Obd2.Core.Elm;

public enum StepResult { Passed, Failed, Skipped }

public sealed record AdapterTestStep(string Name, StepResult Result, string Detail, TimeSpan Elapsed);

public sealed record AdapterTestReport(string Transport, IReadOnlyList<AdapterTestStep> Steps)
{
    /// <summary>True when the adapter itself works (link, reset, setup, voltage).</summary>
    public bool AdapterOk => Steps.Where(s => s.Name != "ECU answers" && s.Name != "Live data").All(s => s.Result == StepResult.Passed);

    /// <summary>True when every step passed, including the vehicle side.</summary>
    public bool AllPassed => Steps.All(s => s.Result == StepResult.Passed);

    public string Summary =>
        AllPassed ? "Adapter and vehicle link are working."
        : AdapterOk ? "The adapter works, but the vehicle did not answer. Turn the ignition on and check the protocol."
        : "The adapter did not pass. See the failed step below.";
}

/// <summary>
/// Step-by-step adapter check: link, reset, setup commands, supply voltage, protocol, ECU and a live value.
/// Read-only: only commands on the ElmClient allowlist are used.
/// </summary>
public static class AdapterTester
{
    private static readonly Regex Volts = new(@"([0-9]+(\.[0-9]+)?)", RegexOptions.Compiled);

    public static AdapterTestReport Run(IElmTransport transport, int protocol, Action<AdapterTestStep>? progress = null)
    {
        var steps = new List<AdapterTestStep>();
        string name = transport.DisplayName;
        bool continueTest = true;

        void Record(string title, Func<(bool Ok, string Detail)> action, bool required = true)
        {
            if (!continueTest)
            {
                var skipped = new AdapterTestStep(title, StepResult.Skipped, "Skipped because an earlier step failed.", TimeSpan.Zero);
                steps.Add(skipped);
                progress?.Invoke(skipped);
                return;
            }

            var clock = Stopwatch.StartNew();
            AdapterTestStep step;
            try
            {
                var (ok, detail) = action();
                step = new AdapterTestStep(title, ok ? StepResult.Passed : StepResult.Failed, detail, clock.Elapsed);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException
                                          or UnauthorizedAccessException or System.Net.Sockets.SocketException)
            {
                step = new AdapterTestStep(title, StepResult.Failed, ex.Message, clock.Elapsed);
            }

            if (step.Result == StepResult.Failed && required)
                continueTest = false;

            steps.Add(step);
            progress?.Invoke(step);
        }

        using var client = new ElmClient(transport);

        Record("Open link", () =>
        {
            client.Open();
            return (true, name);
        });

        Record("Adapter reset (ATZ)", () =>
        {
            var r = client.SendAt("ATZ", TimeSpan.FromSeconds(6));
            string id = r.Lines.FirstOrDefault(l => l.Contains("ELM", StringComparison.OrdinalIgnoreCase)) ?? r.Lines.FirstOrDefault() ?? "";
            return id.Length > 0 && !r.HasError
                ? (true, id)
                : (false, "No valid reply. Check the baud rate, pairing or Wi-Fi network.");
        });

        Record("Setup commands", () =>
        {
            foreach (var cmd in new[] { "ATE0", "ATL0", "ATS0", "ATH0", "ATCAF1" })
            {
                var r = client.SendAt(cmd);
                if (r.HasError) return (false, $"{cmd} was rejected ({r.Error}).");
            }
            return (true, "ATE0, ATL0, ATS0, ATH0, ATCAF1 accepted");
        });

        Record("Supply voltage (ATRV)", () =>
        {
            var r = client.SendAt("ATRV");
            var m = r.Lines.Count > 0 ? Volts.Match(r.Lines[0]) : Match.Empty;
            if (!m.Success || !double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return (false, "The adapter did not report a voltage.");

            return (true, v < 11.5
                ? $"{v:0.0} V (low: ignition off, or the OBD-II port has poor power)"
                : $"{v:0.0} V");
        }, required: false);

        Record("Vehicle protocol", () =>
        {
            var set = client.SendAt($"ATSP{protocol}");
            var dp = client.SendAt("ATDP");
            string text = dp.Lines.FirstOrDefault(l => !l.Equals("OK", StringComparison.OrdinalIgnoreCase)) ?? "";
            return set.HasError ? (false, "The adapter rejected the protocol setting.") : (true, text.Length > 0 ? text : $"Protocol {protocol}");
        });

        Record("ECU answers", () =>
        {
            var r = client.SendObd("0100", TimeSpan.FromSeconds(12));
            bool ok = r.HexLines.Any(l => l.StartsWith("4100", StringComparison.Ordinal));
            return ok
                ? (true, "The engine ECU answered (supported PIDs received).")
                : (false, r.HasError
                    ? $"{r.Error}. Turn the ignition on, plug the adapter in fully, and check the protocol."
                    : "No ECU reply.");
        }, required: false);

        bool ecuOk = steps.Any(s => s.Name == "ECU answers" && s.Result == StepResult.Passed);
        continueTest = continueTest && ecuOk;
        if (!ecuOk && steps.Any(s => s.Name == "ECU answers"))
            continueTest = false;

        Record("Live data", () =>
        {
            var r = client.SendObd("010C");
            var line = r.HexLines.FirstOrDefault(l => l.StartsWith("410C", StringComparison.Ordinal));
            if (line is null || line.Length < 8) return (false, "No engine speed reply.");
            double rpm = (Convert.ToInt32(line.Substring(4, 2), 16) * 256 + Convert.ToInt32(line.Substring(6, 2), 16)) / 4.0;
            return (true, $"Engine speed {rpm:0} rpm");
        }, required: false);

        return new AdapterTestReport(name, steps);
    }
}
