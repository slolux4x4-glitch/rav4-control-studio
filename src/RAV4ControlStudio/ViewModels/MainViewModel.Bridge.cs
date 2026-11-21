using System.Diagnostics;
using System.IO;
using Obd2.Core.Bridge;

namespace RAV4ControlStudio.ViewModels;

/// <summary>
/// RAV4 Bridge: the laptop next to the car shares its adapter with this app on another PC.
/// On the laptop, Settings &gt; Laptop bridge writes bridge.json and starts RAV4Bridge.exe.
/// On the other PC, OBD2 Connection &gt; Laptop bridge connects to it.
/// </summary>
public sealed partial class MainViewModel
{
    // ----- this PC connects through a laptop -----

    private string _bridgeHost = "";
    public string BridgeHost { get => _bridgeHost; set => Set(ref _bridgeHost, value ?? ""); }

    private int _bridgePort = BridgeConfig.DefaultPort;
    public int BridgePort { get => _bridgePort; set => Set(ref _bridgePort, value); }

    private string _bridgeCode = "";
    public string BridgeCode { get => _bridgeCode; set => Set(ref _bridgeCode, (value ?? "").Trim()); }

    // ----- this PC is the laptop: share its adapter -----

    private int _bridgeListenPort = BridgeConfig.DefaultPort;
    public int BridgeListenPort { get => _bridgeListenPort; set => Set(ref _bridgeListenPort, value); }

    private string _bridgeAccessCode = BridgeConfig.NewCode();
    public string BridgeAccessCode { get => _bridgeAccessCode; private set => Set(ref _bridgeAccessCode, value); }

    private string _bridgeStatus = "";
    public string BridgeStatus { get => _bridgeStatus; private set => Set(ref _bridgeStatus, value); }

    public RelayCommand SaveBridgeSettingsCommand { get; private set; } = null!;
    public RelayCommand StartBridgeCommand { get; private set; } = null!;
    public RelayCommand NewBridgeCodeCommand { get; private set; } = null!;

    internal static string BridgeConfigPath => Path.Combine(AppFolder, "bridge.json");

    private static string BridgeExe => Path.Combine(AppContext.BaseDirectory, "RAV4Bridge.exe");

    private void InitBridgeCommands()
    {
        SaveBridgeSettingsCommand = new RelayCommand(() => SaveBridgeConfig());
        StartBridgeCommand = new RelayCommand(StartBridge);
        NewBridgeCodeCommand = new RelayCommand(() =>
        {
            BridgeAccessCode = BridgeConfig.NewCode();
            BridgeStatus = "New access code. Press Save bridge settings (and restart RAV4 Bridge if it is running).";
        });
    }

    private void LoadBridgeConfig()
    {
        var saved = BridgeConfig.Load(BridgeConfigPath);
        if (saved is null)
        {
            _bridgeStatus = "Not set up. Choose how this laptop reaches the adapter on the OBD2 Connection page, then press Save bridge settings.";
            return;
        }
        _bridgeListenPort = saved.ListenPort;
        _bridgeAccessCode = saved.Code;
        _bridgeStatus = $"Saved: adapter {AdapterText(saved)}, bridge port {saved.ListenPort}.";
    }

    private static string AdapterText(BridgeConfig c) =>
        c.AdapterMode == "Serial" ? $"{c.SerialPort} at {c.Baud} baud" : $"Wi-Fi {c.AdapterHost}:{c.AdapterPort}";

    /// <summary>The bridge uses the adapter this PC is set up for on the OBD2 Connection page (Wi-Fi or Bluetooth/USB).</summary>
    internal BridgeConfig? BuildBridgeConfig(out string problem)
    {
        problem = "";
        var config = new BridgeConfig { ListenPort = BridgeListenPort, Code = BridgeAccessCode };
        switch (SelectedMode)
        {
            case ConnectionMode.Wifi:
                config.AdapterMode = "Wifi";
                config.AdapterHost = WifiHost.Trim();
                config.AdapterPort = WifiPort;
                break;
            case ConnectionMode.Serial:
                config.AdapterMode = "Serial";
                config.SerialPort = SelectedPort?.Name;
                config.Baud = SelectedBaud;
                break;
            default:
                problem = "On the OBD2 Connection page, choose Wi-Fi or Bluetooth / USB: that is the adapter the bridge will share.";
                return null;
        }
        var problems = config.Problems();
        if (problems.Count > 0) { problem = string.Join(" ", problems); return null; }
        return config;
    }

    private bool SaveBridgeConfig()
    {
        var config = BuildBridgeConfig(out string problem);
        if (config is null) { BridgeStatus = problem; return false; }
        try
        {
            config.Save(BridgeConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BridgeStatus = "Could not save the bridge settings: " + ex.Message;
            return false;
        }
        BridgeStatus = $"Saved: adapter {AdapterText(config)}, bridge port {config.ListenPort}, code {config.Code}.";
        return true;
    }

    private void StartBridge()
    {
        if (IsConnected || IsBusy)
        {
            BridgeStatus = "Disconnect first: the adapter accepts only one connection, and the bridge needs it.";
            return;
        }
        if (!SaveBridgeConfig()) return;
        if (!File.Exists(BridgeExe))
        {
            BridgeStatus = $"RAV4Bridge.exe was not found next to the app ({AppContext.BaseDirectory}).";
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(BridgeExe, $"\"{BridgeConfigPath}\"") { UseShellExecute = true });
            BridgeStatus = $"RAV4 Bridge started. On the other PC choose Laptop bridge, enter this laptop's address, port {BridgeListenPort} and code {BridgeAccessCode}.";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            BridgeStatus = "Could not start RAV4 Bridge: " + ex.Message;
        }
    }
}
