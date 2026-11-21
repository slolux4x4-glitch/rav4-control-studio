using System.IO;
using System.Text.Json;
using RAV4ControlStudio.ViewModels;

namespace RAV4ControlStudio.Services;

/// <summary>Remembered between launches: connection choices, units and polling delay.</summary>
public sealed class AppSettings
{
    public string Mode { get; set; } = "Serial";
    public string WifiHost { get; set; } = "192.168.0.10";
    public int WifiPort { get; set; } = 35000;
    public int Baud { get; set; } = 38400;
    public int Protocol { get; set; } = 6;
    public string? Port { get; set; }
    public bool Imperial { get; set; }
    public int PollDelayMs { get; set; } = 15;
    public bool SimulateFaults { get; set; }
    public string BridgeHost { get; set; } = "";
    public int BridgePort { get; set; } = Obd2.Core.Bridge.BridgeConfig.DefaultPort;
    public string BridgeCode { get; set; } = "";
    public bool AutoReconnect { get; set; } = true;
    public bool AlertsEnabled { get; set; } = true;
    public double AlertCoolantC { get; set; } = 105;
    public double AlertVoltageMin { get; set; } = 11.5;
    public double AlertRpmMax { get; set; } = 6500;
    public double UiScale { get; set; } = 1.0;
    public string Vehicle { get; set; } = "My RAV4";
    public List<string> Vehicles { get; set; } = new() { "My RAV4" };

    private static string FilePath => Path.Combine(MainViewModel.AppFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(MainViewModel.AppFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; never fail the app because they could not be saved.
        }
    }
}
