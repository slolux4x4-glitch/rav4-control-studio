using System.Security.Cryptography;
using System.Text.Json;

namespace Obd2.Core.Bridge;

/// <summary>
/// Settings for RAV4 Bridge, the small web server that runs on the laptop next to the car and lets RAV4 Control
/// Studio on another PC use the laptop's adapter. Written by the app's Settings page, read by the bridge program.
/// </summary>
public sealed class BridgeConfig
{
    public const int DefaultPort = 35100;

    /// <summary>How the laptop reaches the adapter: "Wifi" or "Serial".</summary>
    public string AdapterMode { get; set; } = "Wifi";
    public string AdapterHost { get; set; } = "192.168.0.10";
    public int AdapterPort { get; set; } = 35000;
    public string? SerialPort { get; set; }
    public int Baud { get; set; } = 38400;

    /// <summary>Port the bridge listens on for the other PC.</summary>
    public int ListenPort { get; set; } = DefaultPort;

    /// <summary>Access code the other PC must send with every request.</summary>
    public string Code { get; set; } = NewCode();

    public static string NewCode() => RandomNumberGenerator.GetString("abcdefghjkmnpqrstuvwxyz23456789", 10);

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RAV4 Control Studio", "bridge.json");

    /// <summary>Problems that stop the bridge from starting, or an empty list when the settings can be used.</summary>
    public IReadOnlyList<string> Problems()
    {
        var list = new List<string>();
        if (AdapterMode is not ("Wifi" or "Serial")) list.Add("AdapterMode must be Wifi or Serial.");
        if (AdapterMode == "Wifi" && string.IsNullOrWhiteSpace(AdapterHost)) list.Add("The adapter address is empty.");
        if (AdapterMode == "Wifi" && AdapterPort is < 1 or > 65535) list.Add("The adapter port must be 1 to 65535.");
        if (AdapterMode == "Serial" && string.IsNullOrWhiteSpace(SerialPort)) list.Add("No COM port is chosen.");
        if (ListenPort is < 1024 or > 65535) list.Add("The bridge port must be 1024 to 65535.");
        if (string.IsNullOrEmpty(Code) || Code.Length < 8) list.Add("The access code must be at least 8 characters.");
        return list;
    }

    public static BridgeConfig? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
