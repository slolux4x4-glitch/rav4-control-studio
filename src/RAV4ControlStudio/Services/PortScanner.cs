using System.IO.Ports;
using System.Management;

namespace RAV4ControlStudio.Services;

/// <summary>Lists COM ports and labels Bluetooth SPP links separately from wired USB adapters.</summary>
public static class PortScanner
{
    public static IReadOnlyList<(string Name, string Label)> Scan()
    {
        var found = new List<(string Name, string Label)>();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Name FROM Win32_SerialPort");
            foreach (ManagementObject port in searcher.Get())
            {
                string id = port["DeviceID"]?.ToString() ?? "";
                string name = port["Name"]?.ToString() ?? "";
                if (id.Length == 0) continue;

                string kind = name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                    ? "Bluetooth"
                    : "USB / wired";
                found.Add((id, $"{id}  ·  {kind}  ·  {name}"));
            }
        }
        catch (Exception)
        {
            // WMI unavailable: fall back to the plain port list below.
        }

        if (found.Count == 0)
        {
            foreach (var name in SerialPort.GetPortNames())
                found.Add((name, $"{name}  ·  serial port"));
        }

        return found
            .OrderBy(p => int.TryParse(new string(p.Name.Where(char.IsDigit).ToArray()), out var n) ? n : int.MaxValue)
            .ToList();
    }
}
