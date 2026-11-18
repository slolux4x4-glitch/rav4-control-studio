using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using Obd2.Core.Obd;
using RAV4ControlStudio.ViewModels;

namespace RAV4ControlStudio.Services;

public sealed record AboutItem(string Label, string Value);

public sealed record AboutSection(string Title, IReadOnlyList<AboutItem> Items);

/// <summary>Collects the information shown on the About page. Runtime and library versions come from the running process.</summary>
public static class AboutInfo
{
    public static string AppName => "RAV4 Control Studio";

    public static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info)) return info.Split('+')[0];
            return asm.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    public static IReadOnlyList<AboutSection> Collect()
    {
        string exe = Environment.ProcessPath ?? "";
        string built = exe.Length > 0 && File.Exists(exe)
            ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "Unknown";

        var dtc = DtcDatabase.All;
        int toyota = dtc.Count(e => e.Source.StartsWith("Toyota", StringComparison.Ordinal));
        int imported = dtc.Count(e => e.Source.StartsWith("Imported", StringComparison.Ordinal));

        return new List<AboutSection>
        {
            new("Application", new AboutItem[]
            {
                new("Name", AppName),
                new("Version", Version),
                new("Built", built),
                new("Target vehicle", "2021 Toyota RAV4 (M20A-FKS 2.0 L, CVT)"),
                new("Mode", "Read-only diagnostics. Nothing is written to the vehicle."),
            }),

            new("Runtime", new AboutItem[]
            {
                new(".NET runtime", RuntimeInformation.FrameworkDescription),
                new("Runtime version", Environment.Version.ToString()),
                new("Runtime identifier", RuntimeInformation.RuntimeIdentifier),
                new("Process architecture", RuntimeInformation.ProcessArchitecture.ToString()),
                new("Garbage collector", GCSettings.IsServerGC ? "Server" : "Workstation (" + GCSettings.LatencyMode + ")"),
                new("UI framework", "Windows Presentation Foundation (WPF) " + VersionOf(typeof(System.Windows.Window))),
                new("Operating system", RuntimeInformation.OSDescription),
                new("OS architecture", RuntimeInformation.OSArchitecture.ToString()),
                new("Culture", CultureInfo.CurrentUICulture.Name.Length > 0 ? CultureInfo.CurrentUICulture.Name : "Invariant"),
                new("Packaging", IsSelfContained() ? "Self-contained (the .NET runtime is bundled)" : "Framework-dependent (uses the installed .NET runtime)"),
            }),

            new("Libraries", new AboutItem[]
            {
                new("Obd2.Core", VersionOf(typeof(ObdService)) + "  (ELM327 and OBD-II protocol layer, this project)"),
                new("System.IO.Ports", VersionOfName("System.IO.Ports") + "  (COM port and Bluetooth serial access)"),
                new("System.Management", VersionOfName("System.Management") + "  (Windows WMI, used to label Bluetooth and USB ports)"),
                new("System.Text.Json", VersionOf(typeof(System.Text.Json.JsonSerializer)) + "  (settings, code database, imports)"),
                new("System.Net.Sockets", VersionOf(typeof(System.Net.Sockets.TcpClient)) + "  (Wi-Fi adapter link)"),
                new("Windows integration", "WindowChrome (custom title bar), DWM rounded corners (Windows 11)"),
            }),

            new("Data and content", new AboutItem[]
            {
                new("Trouble codes", $"{dtc.Count:N0} entries ({toyota} Toyota-specific built in, {imported:N0} imported by you)"),
                new("Code database source", "OBDex generic SAE J2012 data (CC0 public domain): titles, explanations, causes, symptoms"),
                new("Live parameters (PIDs)", $"{PidCatalog.All.Count} Mode 01 parameters with standard SAE J1979 formulas"),
                new("OBD-II services used", "01 live data, 02 freeze frame, 03 stored codes, 07 pending codes, 09 vehicle info, 0A permanent codes"),
                new("VIN decoding", "ISO 3779 check digit, model year and a small manufacturer table (offline)"),
            }),

            new("Standards and protocols", new AboutItem[]
            {
                new("SAE J1979", "OBD-II diagnostic services and parameters"),
                new("SAE J2012", "Diagnostic trouble code format and definitions"),
                new("ISO 15765-4", "OBD-II over CAN (11-bit, 500 kbaud on the 2021 RAV4)"),
                new("ELM327 command set", "Adapter control: AT commands, with a read-only allowlist"),
                new("Connections", "Serial COM (USB and Bluetooth SPP), Wi-Fi TCP, built-in simulator"),
            }),

            new("Credits and notices", new AboutItem[]
            {
                new("Trouble code data", "OBDex project (github.com/foerbsnavi/OBDex), CC0 1.0"),
                new("Concept", "Layout and artwork follow the RAV4 Control Studio concept supplied by the project owner"),
                new("Trademarks", "Toyota and RAV4 are trademarks of Toyota Motor Corporation. This app is independent and not affiliated with or endorsed by Toyota."),
                new("Inspiration", "OBDium (GPL-3.0) inspired the feature set. No OBDium code is used."),
                new("Disclaimer", "For information only. It does not replace diagnosis by a qualified technician."),
            }),

            new("Files and folders", new AboutItem[]
            {
                new("Program", exe.Length > 0 ? exe : "Unknown"),
                new("Data folder", MainViewModel.AppFolder),
                new("Settings", Path.Combine(MainViewModel.AppFolder, "settings.json")),
                new("Logs", MainViewModel.LogsFolder),
                new("Reports", MainViewModel.ReportsFolder),
                new("Imported codes", MainViewModel.CodesFolder),
            }),
        };
    }

    public static string ToText(IEnumerable<AboutSection> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppName} {Version}");
        foreach (var section in sections)
        {
            sb.AppendLine().AppendLine(section.Title.ToUpperInvariant());
            foreach (var item in section.Items)
                sb.AppendLine($"  {item.Label + ":",-26} {item.Value}");
        }
        return sb.ToString();
    }

    private static string VersionOf(Type type) => type.Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private static string VersionOfName(string assemblyName)
    {
        var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName);
        if (asm is null)
        {
            try { asm = Assembly.Load(assemblyName); }
            catch (Exception ex) when (ex is IOException or BadImageFormatException) { return "not loaded"; }
        }
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return info is { Length: > 0 } ? info.Split('+')[0] : asm.GetName().Version?.ToString(3) ?? "unknown";
    }

    private static bool IsSelfContained() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll")) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "hostfxr.dll"));
}
