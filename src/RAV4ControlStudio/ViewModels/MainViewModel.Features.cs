using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Obd2.Core.Elm;
using Obd2.Core.Obd;
using RAV4ControlStudio.Services;

namespace RAV4ControlStudio.ViewModels;

public sealed record WifiPreset(string Label, string Host, int Port)
{
    public override string ToString() => Label;
}

public sealed record UiScaleOption(double Scale, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One selectable line on the Trends page. Each has its own rolling series, sampled only while tracked.</summary>
public sealed class TrendItem : ObservableObject
{
    private readonly Action<TrendItem> _changed;
    private bool _selected;

    public TrendItem(PidValue pid, Brush brush, ChartSeries series, Action<TrendItem> changed)
    {
        Pid = pid;
        Brush = brush;
        Series = series;
        _changed = changed;
    }

    public PidValue Pid { get; }
    public Brush Brush { get; }
    public ChartSeries Series { get; }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                _changed(this);
        }
    }
}

/// <summary>Current value of a user-defined (custom) parameter.</summary>
public sealed class CustomValue : ObservableObject
{
    private double _value = double.NaN;

    public CustomValue(CustomParameter definition) => Definition = definition;

    public CustomParameter Definition { get; }

    public string Name => Definition.Name;

    public string Source => $"{Definition.Service} {Definition.Id}" + (Definition.Header is null ? "" : $"  ECU {Definition.Header}");

    public double Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value))
                Raise(nameof(Display));
        }
    }

    public string Display => double.IsNaN(_value) ? "—" : Definition.Display(_value);
}

public sealed partial class MainViewModel
{
    // ===================================================== vehicles and folders

    public const string DefaultVehicle = "My RAV4";

    public static string CurrentVehicle { get; private set; } = DefaultVehicle;

    /// <summary>The default vehicle keeps the original folders; other vehicles get their own subfolder.</summary>
    public static string VehicleFolder =>
        CurrentVehicle == DefaultVehicle
            ? AppFolder
            : Path.Combine(AppFolder, "Vehicles", VehicleFolderName(CurrentVehicle));

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A vehicle name turned into one safe folder name. Characters Windows does not allow become "_"; trailing dots
    /// and spaces (which Windows drops) are removed; and names Windows treats specially ("..", "CON", "NUL" …) get a
    /// leading "_", so a vehicle's folder always stays inside the Vehicles folder and never points at a device.
    /// </summary>
    internal static string VehicleFolderName(string vehicle)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = string.Concat(vehicle.Select(c => invalid.Contains(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
        string stem = name.Split('.')[0].Trim();
        return name.Length == 0 || ReservedNames.Contains(stem) ? "_" + name : name;
    }

    public static string ParametersFolder => Path.Combine(AppFolder, "Parameters");

    public ObservableCollection<string> Vehicles { get; } = new();

    private string _selectedVehicle = DefaultVehicle;

    public string SelectedVehicle
    {
        get => _selectedVehicle;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !Set(ref _selectedVehicle, value)) return;

            CurrentVehicle = value;
            RefreshLogFiles();
            RefreshReportFiles();
            LoadBaseline();
            Raise(nameof(VehicleFolderText));
        }
    }

    public string VehicleFolderText => VehicleFolder;

    public string NewVehicleName { get; set; } = "";

    private void AddVehicle()
    {
        string name = NewVehicleName.Trim();
        if (name.Length == 0 || Vehicles.Contains(name, StringComparer.OrdinalIgnoreCase)) return;

        Vehicles.Add(name);
        SelectedVehicle = name;
        NewVehicleName = "";
        Raise(nameof(NewVehicleName));
        Notify($"Vehicle \"{name}\" added. Logs, reports and baseline are now kept separately for it.");
    }

    // ===================================================== persisted options

    private static readonly HashSet<string> PersistedNames = new()
    {
        nameof(SelectedMode), nameof(WifiHost), nameof(WifiPort), nameof(SelectedBaud), nameof(SelectedProtocol),
        nameof(SelectedPort), nameof(PollDelayMs), nameof(SimulateFaults), nameof(UseImperial), nameof(AutoReconnect),
        nameof(AlertsEnabled), nameof(AlertCoolantC), nameof(AlertVoltageMin), nameof(AlertRpmMax), nameof(SelectedUiScale),
        nameof(SelectedVehicle),
    };

    private bool _autoReconnect = true;
    public bool AutoReconnect { get => _autoReconnect; set => Set(ref _autoReconnect, value); }

    private bool _alertsEnabled = true;
    public bool AlertsEnabled { get => _alertsEnabled; set => Set(ref _alertsEnabled, value); }

    private double _alertCoolantC = 105;
    public double AlertCoolantC { get => _alertCoolantC; set => Set(ref _alertCoolantC, Math.Clamp(value, 60, 150)); }

    private double _alertVoltageMin = 11.5;
    public double AlertVoltageMin { get => _alertVoltageMin; set => Set(ref _alertVoltageMin, Math.Clamp(value, 6, 14)); }

    private double _alertRpmMax = 6500;
    public double AlertRpmMax { get => _alertRpmMax; set => Set(ref _alertRpmMax, Math.Clamp(value, 1000, 9000)); }

    public IReadOnlyList<UiScaleOption> UiScaleOptions { get; } = new[]
    {
        new UiScaleOption(0.9, "Small (90%)"),
        new UiScaleOption(1.0, "Normal (100%)"),
        new UiScaleOption(1.1, "Large (110%)"),
        new UiScaleOption(1.25, "Extra large (125%)"),
    };

    private UiScaleOption _uiScale = null!;

    public UiScaleOption SelectedUiScale
    {
        get => _uiScale;
        set
        {
            if (value is not null && Set(ref _uiScale, value))
                Raise(nameof(UiScale));
        }
    }

    public double UiScale => _uiScale?.Scale ?? 1.0;

    private static double SafeClamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private void InitFeatures(AppSettings saved)
    {
        _autoReconnect = saved.AutoReconnect;
        _alertsEnabled = saved.AlertsEnabled;
        // Same limits as the settings page, so a hand-edited settings.json cannot switch an alert off by accident.
        _alertCoolantC = SafeClamp(saved.AlertCoolantC, 60, 150, 105);
        _alertVoltageMin = SafeClamp(saved.AlertVoltageMin, 6, 14, 11.5);
        _alertRpmMax = SafeClamp(saved.AlertRpmMax, 1000, 9000, 6500);
        _uiScale = UiScaleOptions.FirstOrDefault(o => Math.Abs(o.Scale - saved.UiScale) < 0.01) ?? UiScaleOptions[1];

        foreach (var v in (saved.Vehicles ?? new()).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct()) Vehicles.Add(v);
        if (!Vehicles.Contains(DefaultVehicle)) Vehicles.Insert(0, DefaultVehicle);
        _selectedVehicle = Vehicles.Contains(saved.Vehicle) ? saved.Vehicle : DefaultVehicle;
        CurrentVehicle = _selectedVehicle;
        Vehicles.CollectionChanged += (_, _) => SaveSettings();
    }

    private void AddFeatureSettings(AppSettings s)
    {
        s.AutoReconnect = AutoReconnect;
        s.AlertsEnabled = AlertsEnabled;
        s.AlertCoolantC = AlertCoolantC;
        s.AlertVoltageMin = AlertVoltageMin;
        s.AlertRpmMax = AlertRpmMax;
        s.UiScale = UiScale;
        s.Vehicle = SelectedVehicle;
        s.Vehicles = Vehicles.ToList();
    }

    // ===================================================== commands for the new features

    public RelayCommand AddVehicleCommand { get; private set; } = null!;
    public RelayCommand SaveBaselineCommand { get; private set; } = null!;
    public RelayCommand ClearBaselineCommand { get; private set; } = null!;
    public RelayCommand ReplayLogCommand { get; private set; } = null!;
    public RelayCommand CreatePdfReportCommand { get; private set; } = null!;
    public RelayCommand FindAdapterCommand { get; private set; } = null!;
    public RelayCommand ScanEcusCommand { get; private set; } = null!;
    public RelayCommand CancelScanCommand { get; private set; } = null!;
    public RelayCommand OpenParametersFolderCommand { get; private set; } = null!;
    public RelayCommand ReloadParametersCommand { get; private set; } = null!;
    public RelayCommand<TrendItem> ClearTrendsCommand { get; private set; } = null!;

    private void InitFeatureCommands()
    {
        AddVehicleCommand = new RelayCommand(AddVehicle);
        SaveBaselineCommand = new RelayCommand(SaveBaseline, () => IsConnected);
        ClearBaselineCommand = new RelayCommand(ClearBaseline);
        ReplayLogCommand = new RelayCommand(ReplaySelectedLog, () => SelectedLogFile is not null && !IsConnected && !IsBusy);
        CreatePdfReportCommand = new RelayCommand(CreatePdfReport);
        FindAdapterCommand = new RelayCommand(() => Fire(FindAdapterAsync()), () => !IsFinding);
        ScanEcusCommand = new RelayCommand(() => Fire(ScanEcusAsync()), () => IsConnected && !IsScanning);
        CancelScanCommand = new RelayCommand(() => _scanCts?.Cancel(), () => IsScanning);
        OpenParametersFolderCommand = new RelayCommand(OpenParametersFolder);
        ReloadParametersCommand = new RelayCommand(ReloadCustomParameters);
        ClearTrendsCommand = new RelayCommand<TrendItem>(_ => { foreach (var t in TrendItems) t.IsSelected = false; });
        SaveSessionLogCommand = new RelayCommand(SaveSessionLog);
        ClearCodesCommand = new RelayCommand(() => Fire(ClearCodesAsync()), CanClearCodes);
    }

    // ===================================================== clear codes (written, switched off)

    /// <summary>
    /// App switch for the Clear codes button. OFF: the button stays greyed out and the flow below never runs.
    /// It is one of two switches; the other (Obd2.Core ElmClient.ClearCodesEnabled) must also be on before
    /// anything can be sent. Turn both on only on the owner's explicit instruction.
    /// </summary>
    internal const bool ClearCodesEnabled = false;

    public string ClearCodesNote => ClearCodesEnabled
        ? "Clear stored and pending fault codes. Engine off, ignition on. This also resets the readiness monitors."
        : "Clearing codes is not available yet. This app is read-only for now.";

    public RelayCommand ClearCodesCommand { get; private set; } = null!;

    // Only with a real car (not the demo or a log replay), connected, and nothing else using the adapter.
    private bool CanClearCodes() =>
        ClearCodesEnabled && State == LinkState.Connected && _obd is not null && !IsBusy && !IsScanning;

    /// <summary>Warns, asks for confirmation, clears, then re-reads the codes so the page shows the result.</summary>
    private async Task ClearCodesAsync()
    {
        var obd = _obd;
        if (!CanClearCodes() || obd is null) { Notify(ClearCodesNote); return; }

        var codes = string.Join(", ", StoredDtcs.Concat(PendingDtcs).Select(d => d.Code).Distinct());
        var answer = MessageBox.Show(
            "Clear the fault codes in the engine ECU?\n\n" +
            (codes.Length > 0 ? $"Codes now: {codes}\n\n" : "") +
            "This erases stored and pending codes and the freeze frame, turns off the check-engine light, and " +
            "resets the readiness monitors. The car may then fail an emissions inspection until it has been " +
            "driven for a few days. It does not fix the fault: if it is still there, the code will come back.\n\n" +
            "The engine must be off with the ignition on.",
            "Clear fault codes", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) { Notify("Clear codes cancelled."); return; }

        _trace.Note($"Clear codes requested (codes: {(codes.Length > 0 ? codes : "none")})");
        try
        {
            var result = await Task.Run(obd.ClearCodes);
            _trace.Note("Clear codes: " + result.Message);
            Notify(result.Message);
            await RefreshDiagnosticsAsync();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
        {
            _trace.Note("Clear codes failed: " + ex.Message);
            Notify("Could not clear codes: " + ex.Message);
        }
    }

    // ===================================================== session log (every command and reply)

    // One trace for the whole run, so a failed connect, the drop-out and the reconnect attempts all end up in one file.
    private readonly SessionTrace _trace = new();

    public RelayCommand SaveSessionLogCommand { get; private set; } = null!;

    /// <summary>Writes every command sent to the adapter and its exact reply to a text file in the Logs folder.</summary>
    public void SaveSessionLog()
    {
        try
        {
            Directory.CreateDirectory(LogsFolder);
            var path = Path.Combine(LogsFolder, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
                WriteSessionLog(writer);
            Notify($"Session log saved: {path}");
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Notify($"Could not save the session log: {ex.Message}");
        }
    }

    internal void WriteSessionLog(TextWriter writer)
    {
        writer.WriteLine("RAV4 Control Studio session log");
        writer.WriteLine($"Saved:       {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"App:         {typeof(MainViewModel).Assembly.GetName().Version} on {Environment.OSVersion}");
        writer.WriteLine($"State:       {ConnectionText} ({ModeBadgeTitle})");
        writer.WriteLine($"Connection:  {SelectedMode}, Wi-Fi {WifiHost}:{WifiPort}, port {SelectedPort?.ToString() ?? "-"}, {SelectedBaud} baud");
        writer.WriteLine($"Protocol:    {SelectedProtocol?.Label}, poll delay {PollDelayMs} ms");
        writer.WriteLine($"Adapter:     {AdapterId}");
        writer.WriteLine();
        writer.WriteLine("TX = sent to the adapter, RX = its reply exactly as received (\\r = carriage return), !! = timeout or error.");
        writer.WriteLine("The app only sends read-only requests.");
        writer.WriteLine();
        _trace.WriteTo(writer);
    }

    // ===================================================== auto-reconnect

    private CancellationTokenSource? _reconnectCts;

    public void CancelReconnect() => _reconnectCts?.Cancel();

    private async Task HandleLinkLostAsync(string reason)
    {
        Notify($"Connection lost: {reason}");
        _trace.Note($"Connection lost: {reason}");
        bool wasLogging = IsLogging; // disconnecting stops the log; start a new one if the link comes back
        // Read before disconnecting: Disconnect switches the mode back from Replay to the one used before it, so
        // checking afterwards would "reconnect" a failed log replay to the car.
        bool wasReplay = _sessionMode == ConnectionMode.Replay || SelectedMode == ConnectionMode.Replay;
        await DisconnectAsync();
        if (!_autoReconnect || wasReplay) return;

        _reconnectCts?.Cancel();
        var cts = _reconnectCts = new CancellationTokenSource();
        const int attempts = 6;

        try
        {
            for (int i = 1; i <= attempts; i++)
            {
                Notify($"Connection lost. Reconnecting ({i}/{attempts})...");
                try { await Task.Delay(i * 2000, cts.Token); }
                catch (OperationCanceledException) { return; }

                if (IsConnected || cts.IsCancellationRequested) return;
                await ConnectAsync();
                if (cts.IsCancellationRequested) return;
                if (IsConnected)
                {
                    if (wasLogging && !IsLogging) ToggleLogging(); // continues in a new CSV file
                    Notify(wasLogging ? "Reconnected. Logging continues in a new file." : "Reconnected.");
                    _trace.Note("Reconnected" + (wasLogging ? ", logging resumed" : ""));
                    return;
                }
            }
            Notify("Could not reconnect. Check the adapter, then press Connect ECU.");
        }
        finally
        {
            // Release this attempt's cancellation source (only clear the field if a newer attempt has not replaced it).
            if (ReferenceEquals(_reconnectCts, cts)) _reconnectCts = null;
            cts.Dispose();
        }
    }

    // ===================================================== alerts

    private string _alertText = "";
    public string AlertText => _alertText;
    public bool HasAlerts => _alertText.Length > 0;

    private void EvaluateAlerts()
    {
        var list = new List<string>();
        if (_alertsEnabled)
        {
            if (Coolant.HasValue && Coolant.Value >= _alertCoolantC)
                list.Add($"Coolant temperature high ({Units.Format("°C", Coolant.Value, "0")})");
            if (Voltage.HasValue && Voltage.Value < _alertVoltageMin)
                list.Add($"Battery voltage low ({Voltage.Value:0.0} V)");
            if (Rpm.HasValue && Rpm.Value >= _alertRpmMax)
                list.Add($"Engine speed high ({Rpm.Value:0} rpm)");
        }

        string text = string.Join("   |   ", list);
        if (text == _alertText) return;
        _alertText = text;
        Raise(nameof(AlertText));
        Raise(nameof(HasAlerts));
    }

    // ===================================================== trip summary and fuel economy

    private const double StoichAfr = 14.7, GasolineGramsPerLitre = 740;
    private DateTime _tripStart = DateTime.Now, _lastTick = DateTime.Now;
    private double _tripDistKm, _tripFuelL, _tripCoolantSum;
    private int _tripSamples;
    private bool _tripActive;

    public string LastTripText { get; private set; } = "";

    private void ResetTrip()
    {
        _tripStart = _lastTick = DateTime.Now;
        _tripDistKm = _tripFuelL = _tripCoolantSum = 0;
        _tripSamples = 0;
        _tripActive = true;
    }

    private void TickTrip()
    {
        if (!_tripActive) return;

        var now = DateTime.Now;
        double dt = Math.Clamp((now - _lastTick).TotalSeconds, 0, 2);
        _lastTick = now;

        if (Speed.HasValue) _tripDistKm += Speed.Value * dt / 3600;
        if (Maf.HasValue) _tripFuelL += Maf.Value * dt / (StoichAfr * GasolineGramsPerLitre);
        if (Coolant.HasValue) { _tripCoolantSum += Coolant.Value; _tripSamples++; }
    }

    private static string Economy(double litresPer100Km) =>
        Units.Imperial ? $"{235.215 / litresPer100Km:0.0} mpg" : $"{litresPer100Km:0.0} L/100 km";

    /// <summary>Instant economy, estimated from the MAF sensor and speed (assumes petrol at a 14.7:1 mixture).</summary>
    public string FuelEconomyText
    {
        get
        {
            if (!Maf.HasValue) return "—";
            double litresPerHour = Maf.Value * 3600 / (StoichAfr * GasolineGramsPerLitre);
            if (!Speed.HasValue || Speed.Value < 5)
                return Units.Imperial ? $"{litresPerHour * 0.264172:0.00} gal/h (idle)" : $"{litresPerHour:0.00} L/h (idle)";
            return Economy(litresPerHour / Speed.Value * 100);
        }
    }

    public string TripEconomyText => _tripDistKm >= 0.2 && _tripFuelL > 0 ? Economy(_tripFuelL / _tripDistKm * 100) : "—";

    private void BuildTripSummary()
    {
        if (!_tripActive) return;
        _tripActive = false;

        var duration = DateTime.Now - _tripStart;
        if (duration.TotalSeconds < 10) return;

        double avgCoolant = _tripSamples > 0 ? _tripCoolantSum / _tripSamples : double.NaN;
        var sb = new StringBuilder();
        sb.AppendLine($"Duration:          {(int)duration.TotalMinutes:00}:{duration.Seconds:00}");
        sb.AppendLine($"Distance:          {Units.Format("km", _tripDistKm, "0.0")}");
        sb.AppendLine($"Max speed:         {(_peakSpeedKmh > 0 ? Units.Format("km/h", _peakSpeedKmh, "0") : "—")}");
        sb.AppendLine($"Max engine speed:  {(_peakRpm > 0 ? _peakRpm.ToString("0") + " rpm" : "—")}");
        sb.AppendLine($"Average coolant:   {(double.IsNaN(avgCoolant) ? "—" : Units.Format("°C", avgCoolant, "0"))}");
        sb.AppendLine($"Lowest battery:    {(double.IsNaN(_minVolt) ? "—" : _minVolt.ToString("0.0") + " V")}");
        sb.AppendLine($"Fuel used (est.):  {(Units.Imperial ? $"{_tripFuelL * 0.264172:0.00} gal" : $"{_tripFuelL:0.00} L")}");
        sb.Append($"Average economy:   {TripEconomyText}");
        LastTripText = sb.ToString();
        Raise(nameof(LastTripText));
        Notify("Trip summary saved. See the Reports page.");
    }

    // ===================================================== fuel and O2 assessment

    public string FuelTrimAssessment
    {
        get
        {
            var s = _pids[0x06]; var l = _pids[0x07];
            if (!s.HasValue || !l.HasValue) return "Waiting for fuel trim data. Fuel trims show how much the ECU is correcting the mixture.";

            double total = s.Value + l.Value;
            string t = $"Short-term {s.Value:0.0}% plus long-term {l.Value:0.0}% = {total:0.0}%. ";
            if (Math.Abs(total) <= 10) return t + "Normal: the ECU needs little correction.";

            string dir = total > 0
                ? "The ECU is adding fuel (running lean): look for a vacuum or air leak, weak fuel delivery or a dirty MAF sensor."
                : "The ECU is removing fuel (running rich): look for a leaking injector, high fuel pressure or a faulty sensor.";
            return t + (Math.Abs(total) > 20 ? "Large correction. " : "Worth watching. ") + dir + " These are hints, not a diagnosis.";
        }
    }

    public string LambdaAssessment
    {
        get
        {
            var lam = _pids[0x24];
            if (!lam.HasValue) return "Waiting for air-fuel ratio (lambda) data.";
            double v = lam.Value;
            if (Math.Abs(v - 1.0) <= 0.05) return $"Lambda {v:0.000}: the mixture is at the ideal ratio.";
            return v > 1 ? $"Lambda {v:0.000}: lean (more air than fuel)." : $"Lambda {v:0.000}: rich (more fuel than air).";
        }
    }

    public string CatalystText
    {
        get
        {
            var c = _pids[0x3C];
            return c.HasValue
                ? $"Catalyst temperature {Units.Format("°C", c.Value, "0")}. A working catalyst is normally 400 to 800 °C once warm."
                : "Catalyst temperature not reported by this ECU.";
        }
    }

    // ===================================================== baseline comparison

    private string BaselinePath => Path.Combine(VehicleFolder, "baseline.json");

    public string BaselineInfo { get; private set; } = "No baseline saved";

    private sealed record BaselineFile(DateTime Saved, Dictionary<string, double> Values);

    private void SaveBaseline()
    {
        var values = new Dictionary<string, double>();
        foreach (var pv in LivePids)
            if (pv.HasValue) values[pv.Pid.ToString("X2")] = pv.Value;

        if (values.Count == 0) { Notify("No live values to save yet."); return; }

        var file = new BaselineFile(DateTime.Now, values);
        try
        {
            Directory.CreateDirectory(VehicleFolder);
            File.WriteAllText(BaselinePath, JsonSerializer.Serialize(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify("Could not save the baseline: " + ex.Message);
            return;
        }
        ApplyBaseline(file);
        Notify($"Baseline saved ({values.Count} values). Live Data now shows the difference from it.");
    }

    private void ClearBaseline()
    {
        try { if (File.Exists(BaselinePath)) File.Delete(BaselinePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        ApplyBaseline(null);
        Notify("Baseline cleared.");
    }

    private void LoadBaseline()
    {
        try
        {
            var file = File.Exists(BaselinePath)
                ? JsonSerializer.Deserialize<BaselineFile>(File.ReadAllText(BaselinePath))
                : null;
            // A hand-edited or damaged file may have no values at all; that is "no baseline", not a crash at startup.
            ApplyBaseline(file?.Values is null ? null : file);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ApplyBaseline(null);
        }
    }

    private void ApplyBaseline(BaselineFile? file)
    {
        foreach (var pv in LivePids)
            pv.Baseline = file is not null && file.Values.TryGetValue(pv.Pid.ToString("X2"), out var v) ? v : double.NaN;

        BaselineInfo = file is null ? "No baseline saved" : $"Baseline saved {file.Saved:yyyy-MM-dd HH:mm}";
        Raise(nameof(BaselineInfo));
    }

    // ===================================================== trends (choose what to graph)

    private static readonly string[] TrendColours = { "#2AA7F5", "#22C55E", "#F59E0B", "#EF4444", "#A78BFA", "#22D3EE" };
    private static readonly byte[] FuelPids = { 0x06, 0x07, 0x14, 0x15, 0x24 };
    private const int MaxTrendLines = 6;

    private readonly Dictionary<byte, TrendItem> _trend = new();
    private HashSet<byte> _tracked = new();
    private byte[] _priorityPids = Array.Empty<byte>();
    private string _currentPage = "Overview";

    public ObservableCollection<TrendItem> TrendItems { get; } = new();

    public IEnumerable<ChartSeries> TrendSeries => TrendItems.Where(t => t.IsSelected).Select(t => t.Series).ToList();

    // The fuel lines never change after start-up, so the lists are built once. A new list on every read made the
    // Fuel page rebuild its rows whenever all bindings were refreshed (for example on connect and disconnect).
    private IReadOnlyList<ChartSeries>? _fuelSeries;
    private IReadOnlyList<TrendItem>? _fuelItems;

    public IEnumerable<ChartSeries> FuelSeries => _fuelSeries ??= FuelPids.Select(p => _trend[p].Series).ToList();

    public IEnumerable<TrendItem> FuelItems => _fuelItems ??= FuelPids.Select(p => _trend[p]).ToList();

    public string TrendHint => TrendItems.Count(t => t.IsSelected) == 0
        ? $"Tick up to {MaxTrendLines} parameters to graph them. Each line is scaled to its own range."
        : "Each line is scaled to the parameter's own range, so shapes are comparable. Values are shown on the right.";

    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            if (Set(ref _currentPage, value))
                UpdatePriority();
        }
    }

    private void InitTrends()
    {
        int i = 0;
        foreach (var pv in LivePids)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(TrendColours[i % TrendColours.Length]));
            brush.Freeze();
            var series = new ChartSeries(pv.Name, brush, pv.Min, pv.Max, ChartCapacity);
            var item = new TrendItem(pv, brush, series, OnTrendSelectionChanged);
            _trend[pv.Pid] = item;
            TrendItems.Add(item);
            i++;
        }

        // Start with engine speed, coolant temperature and battery voltage ticked so the graph is not empty.
        foreach (var pid in DefaultTrendPids)
            if (_trend.TryGetValue(pid, out var item))
                item.IsSelected = true;
    }

    internal static readonly byte[] DefaultTrendPids = { 0x0C, 0x05, 0x42 };

    private void OnTrendSelectionChanged(TrendItem item)
    {
        if (item.IsSelected && TrendItems.Count(t => t.IsSelected) > MaxTrendLines)
        {
            item.IsSelected = false;
            Notify($"At most {MaxTrendLines} lines can be graphed at once.");
            return;
        }
        UpdatePriority();
        Raise(nameof(TrendSeries));
        Raise(nameof(TrendHint));
        ChartUpdated?.Invoke();
    }

    /// <summary>Works out which extra PIDs the poll loop must read every cycle for the page being shown.</summary>
    private void UpdatePriority()
    {
        var set = new HashSet<byte>();
        if (_currentPage == "Trends")
            foreach (var t in TrendItems.Where(t => t.IsSelected)) set.Add(t.Pid.Pid);
        if (_currentPage == "Fuel")
            foreach (var p in FuelPids) set.Add(p);

        foreach (var pid in set.Where(p => !_tracked.Contains(p)))
            _trend[pid].Series.Clear();

        _tracked = set;
        _priorityPids = set.ToArray();
        if (_currentPage is "Trends" or "Fuel") ChartUpdated?.Invoke();
    }

    private void SampleTrends()
    {
        foreach (var pid in _tracked)
        {
            var item = _trend[pid];
            item.Series.Add(item.Pid.HasValue ? item.Pid.Value : double.NaN);
        }
    }

    // ===================================================== custom parameters

    private CustomParameter[] _customParams = Array.Empty<CustomParameter>();
    // Keyed by the parameter itself, not its name: two files can use the same name for different readings.
    private readonly Dictionary<CustomParameter, double?> _pendingCustom = new();
    private int _customCursor;

    public ObservableCollection<CustomValue> CustomValues { get; } = new();

    public string CustomParameterStatus { get; private set; } = "";

    public bool HasCustomParameters => CustomValues.Count > 0;

    private void ReloadCustomParameters()
    {
        var (list, errors) = CustomParameter.LoadFolder(ParametersFolder);
        CustomValues.Clear();
        foreach (var p in list) CustomValues.Add(new CustomValue(p));
        _customParams = list.ToArray();

        CustomParameterStatus = list.Count == 0
            ? "No custom parameters. Use Custom parameters... to add your own (for example Toyota-specific values)."
            : $"{list.Count} custom parameter(s) loaded" + (errors.Count > 0 ? $", {errors.Count} problem(s): {errors[0]}" : "");
        Raise(nameof(CustomParameterStatus));
        Raise(nameof(HasCustomParameters));
    }

    private void OpenParametersFolder()
    {
        string example = Path.Combine(ParametersFolder, "example.json.txt");
        {
            WriteHelpFile(example,
                "Copy this file to something.json (remove the .txt) and edit it, then press Reload.\n" +
                "Only read-only services are accepted: \"01\" (live data) and \"22\" (read data by identifier).\n" +
                "Formula: A, B, C... are the reply bytes. Header is the ECU request address (optional), for example 7E0.\n\n" +
                "[\n" +
                "  {\n" +
                "    \"name\": \"Engine oil temperature (example)\",\n" +
                "    \"unit\": \"C\",\n" +
                "    \"service\": \"01\",\n" +
                "    \"id\": \"5C\",\n" +
                "    \"bytes\": 1,\n" +
                "    \"formula\": \"A - 40\",\n" +
                "    \"min\": -40, \"max\": 150, \"format\": \"0\"\n" +
                "  },\n" +
                "  {\n" +
                "    \"name\": \"Example data identifier\",\n" +
                "    \"unit\": \"rpm\",\n" +
                "    \"service\": \"22\",\n" +
                "    \"id\": \"01A0\",\n" +
                "    \"header\": \"7E0\",\n" +
                "    \"bytes\": 2,\n" +
                "    \"formula\": \"(A*256+B)/4\"\n" +
                "  }\n" +
                "]\n");
        }
        OpenFolder(ParametersFolder);
    }

    /// <summary>Called from the poll thread: reads one custom parameter per cycle.</summary>
    private void PollCustom(ObdService obd)
    {
        var all = _customParams;
        if (all.Length == 0) return;

        var p = all[_customCursor++ % all.Length];
        double? value = null;
        try { value = obd.ReadCustom(p); }
        catch (TimeoutException) { }

        lock (_readGate) _pendingCustom[p] = value;
    }

    private void ApplyCustom()
    {
        Dictionary<CustomParameter, double?> snapshot;
        lock (_readGate)
        {
            if (_pendingCustom.Count == 0) return;
            snapshot = new Dictionary<CustomParameter, double?>(_pendingCustom);
            _pendingCustom.Clear();
        }
        foreach (var cv in CustomValues)
            if (snapshot.TryGetValue(cv.Definition, out var v)) cv.Value = v ?? double.NaN;
    }

    // ===================================================== per-tick work (called every 200 ms from the UI timer)

    private void TickFeatures()
    {
        TickTrip();
        EvaluateAlerts();
        SampleTrends();
        ApplyCustom();
        if (_remote?.IsRunning == true) _remoteJson = BuildRemoteJson();
    }

    // ===================================================== ECU discovery

    public ObservableCollection<EcuNode> EcuNodes { get; } = new();

    private CancellationTokenSource? _scanCts;

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
            {
                ScanEcusCommand.RaiseCanExecuteChanged();
                CancelScanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _scanStatus = "Connect to the vehicle, then press Scan. Quick scan checks the usual engine, transmission and chassis addresses.";
    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }

    public bool ScanFull { get; set; }

    private async Task ScanEcusAsync()
    {
        var obd = _obd;
        if (obd is null) { ScanStatus = "Connect to the ECU first."; return; }

        IsScanning = true;
        EcuNodes.Clear();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        // The scan changes the adapter's request address. ObdService runs it as one uninterrupted sequence,
        // so live polling simply waits until it is over and can never read from the wrong ECU.
        IEnumerable<int> ids = ScanFull
            ? Enumerable.Range(0x700, 0xF0)
            : Enumerable.Range(0x7E0, 8).Concat(Enumerable.Range(0x7B0, 8)).Concat(Enumerable.Range(0x7C0, 8));

        try
        {
            var found = await Task.Run(() => obd.ScanEcus(ids, (i, n) => _ui.InvokeAsync(() => ScanStatus = $"Scanning address {i} of {n}..."), ct));
            foreach (var node in found) EcuNodes.Add(node);
            ScanStatus = found.Count == 0
                ? "No ECUs answered. The car may need the ignition on, or the adapter may only reach the engine ECU."
                : $"Found {found.Count} ECU(s).";
        }
        catch (OperationCanceledException) { ScanStatus = "Scan cancelled."; }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
        {
            ScanStatus = "Scan stopped: " + ex.Message;
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            IsScanning = false;
        }
    }

    // ===================================================== Wi-Fi presets and finder

    public IReadOnlyList<WifiPreset> WifiPresets { get; } = new[]
    {
        new WifiPreset("Most adapters  (192.168.0.10, port 35000)", "192.168.0.10", 35000),
        new WifiPreset("Some adapters  (192.168.0.10, port 23)", "192.168.0.10", 23),
        new WifiPreset("Other adapters  (192.168.4.1, port 35000)", "192.168.4.1", 35000),
    };

    private WifiPreset? _selectedPreset;

    public WifiPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!Set(ref _selectedPreset, value) || value is null) return;
            WifiHost = value.Host;
            WifiPort = value.Port;
        }
    }

    private bool _isFinding;
    public bool IsFinding
    {
        get => _isFinding;
        private set
        {
            if (Set(ref _isFinding, value))
                FindAdapterCommand.RaiseCanExecuteChanged();
        }
    }

    private string _finderStatus = "Join the adapter's Wi-Fi network first, then press Find adapter.";
    public string FinderStatus { get => _finderStatus; private set => Set(ref _finderStatus, value); }

    private async Task FindAdapterAsync()
    {
        IsFinding = true;
        FinderStatus = "Searching for an ELM327 adapter...";
        try
        {
            var found = await AdapterFinder.FindAsync();
            if (found.Count == 0)
            {
                FinderStatus = "No ELM327 adapter found. Check that this PC is on the adapter's Wi-Fi network and the adapter is plugged in.";
                return;
            }
            SelectedMode = ConnectionMode.Wifi;
            WifiHost = found[0].Host;
            WifiPort = found[0].Port;
            FinderStatus = $"Found an ELM327 at {found[0].Host}, port {found[0].Port}. Press Connect ECU.";
        }
        finally
        {
            IsFinding = false;
        }
    }

    // ===================================================== log replay

    public string ReplayPath { get; private set; } = "";
    private ConnectionMode _modeBeforeReplay = ConnectionMode.Serial;

    private void ReplaySelectedLog()
    {
        if (IsConnected || IsBusy) { Notify("Disconnect first, then replay a log."); return; }
        if (SelectedLogFile is null) { Notify("Select a saved log first."); return; }

        ReplayPath = SelectedLogFile.Path;
        if (SelectedMode != ConnectionMode.Replay) _modeBeforeReplay = SelectedMode;
        SelectedMode = ConnectionMode.Replay;
        NavigationRequested?.Invoke("Overview");
        Fire(ConnectAsync());
    }

    private void RestoreModeAfterReplay()
    {
        if (SelectedMode == ConnectionMode.Replay)
            SelectedMode = _modeBeforeReplay;
    }

    // ===================================================== PDF report

    private void CreatePdfReport()
    {
        try
        {
            Directory.CreateDirectory(ReportsFolder);
            string path = Path.Combine(ReportsFolder, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");
            var lines = BuildReport().Split('\n').Select(l => l.TrimEnd('\r'));
            File.WriteAllBytes(path, SimplePdf.FromText("RAV4 Control Studio - Diagnostic report", lines));
            LastReportPath = path;
            RefreshReportFiles();
            Notify($"PDF report saved: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"Could not save the PDF: {ex.Message}");
        }
    }

    // ===================================================== remote view (phone)

    private RemoteViewServer? _remote;
    private string _remoteJson = "{}";
    private bool _remoteEnabled;

    public string RemoteViewUrl { get; private set; } = "Off";

    public bool RemoteViewEnabled
    {
        get => _remoteEnabled;
        set
        {
            if (!Set(ref _remoteEnabled, value)) return;
            ApplyRemoteView();
        }
    }

    private void ApplyRemoteView()
    {
        if (_remoteEnabled)
        {
            _remote ??= new RemoteViewServer();
            _remoteJson = BuildRemoteJson(); // valid from the first request, even before connecting
            _remote.Snapshot = () => _remoteJson;
            if (!_remote.IsRunning && !_remote.Start(8765, out string error))
            {
                _remoteEnabled = false;
                RemoteViewUrl = "Could not start: " + error;
                Raise(nameof(RemoteViewEnabled));
            }
            else
            {
                var ip = RemoteViewServer.LocalAddresses().FirstOrDefault() ?? "this-pc";
                RemoteViewUrl = $"http://{ip}:{_remote.Port}/?t={_remote.Token}";
            }
        }
        else
        {
            // Throw the server away, not just stop it: the next one gets a new access code, so turning the phone
            // view off and on locks out anyone who saw the old address.
            _remote?.Dispose();
            _remote = null;
            RemoteViewUrl = "Off";
        }
        Raise(nameof(RemoteViewUrl));
    }

    private string BuildRemoteJson() => JsonSerializer.Serialize(new
    {
        connected = IsConnected,
        rpm = Rpm.HasValue ? Rpm.Value.ToString("0") : "--",
        speed = Speed.HasValue ? SpeedShown.ToString("0") : "--",
        speedUnit = SpeedUnitText,
        coolant = Coolant.HasValue ? CoolantShown.ToString("0") : "--",
        tempUnit = TempUnitText,
        volt = Voltage.HasValue ? Voltage.Value.ToString("0.0") : "--",
        throttle = Throttle.HasValue ? Throttle.Value.ToString("0") : "--",
        load = Load.HasValue ? Load.Value.ToString("0") : "--",
    });
}
