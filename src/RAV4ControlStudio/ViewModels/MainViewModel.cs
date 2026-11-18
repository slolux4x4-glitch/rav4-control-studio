using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Obd2.Core.Elm;
using Obd2.Core.Obd;
using Obd2.Core.Transport;
using RAV4ControlStudio.Services;

namespace RAV4ControlStudio.ViewModels;

public enum LinkState { Disconnected, Connecting, Connected, Simulated, Replay, Error }

public enum ConnectionMode { Serial, Wifi, Demo, Replay }

public sealed record ProtocolOption(int Number, string Label)
{
    public override string ToString() => Label;
}

public sealed record PortOption(string Name, string Label)
{
    public override string ToString() => Label;
}

public sealed record MonitorRow(string Name, string StatusText, Brush StatusBrush);

/// <summary>One frame line in the CAN Bus Monitor (ID, DLC and data as the concept art shows).</summary>
public sealed record CanRow(string TimeMs, string Id, int Dlc, string Data, bool IsRequest);

public sealed record CategoryOption(char? Letter, string Label)
{
    public override string ToString() => Label;
}

public sealed record TestStepRow(string Name, string Detail, string ResultText, Brush ResultBrush);

public sealed record FileItem(string Name, string Path, string Details);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public const string VehicleEngine = "M20A-FKS (2.0L)";
    public const string VehicleTransmission = "CVT";

    private const int ChartCapacity = 1000; // 5 samples per second, a little over the 3 minute graph window
    private const int CanCapacity = 60;

    private sealed record Snapshot(
        IReadOnlyList<DtcInfo> Stored,
        IReadOnlyList<DtcInfo> Pending,
        ReadinessStatus? Readiness,
        IReadOnlyList<DtcInfo> Permanent,
        FreezeFrameData? Freeze);

    private readonly Dispatcher _ui;
    private readonly Brush _good, _warn, _bad, _muted, _accent, _blue;
    private readonly Dictionary<byte, PidValue> _pids = new();
    private readonly List<double> _chartTimes = new();

    // Poll thread -> UI hand-off. Values and traffic are buffered and applied together at 5 Hz,
    // instead of one dispatcher call (and redraw) per request.
    private readonly object _readGate = new();
    private readonly Dictionary<byte, double> _pendingReadings = new();
    private readonly ConcurrentQueue<TrafficEntry> _trafficQueue = new();
    private DispatcherTimer? _uiTimer;

    private ObdService? _obd;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private CsvLogger? _logger;
    private VinInfo? _vin;
    private int _activeProtocol;
    private int _pollRotation;
    private bool _dtcsLoaded;
    private bool _readinessLoaded;
    private DateTime _sessionStart = DateTime.Now;
    private int _pollDelayMs = 15;
    private bool _loading;
    private string? _savedPort;

    public MainViewModel()
    {
        _ui = Application.Current.Dispatcher;
        _good = Res("GoodBrush");
        _warn = Res("WarnBrush");
        _bad = Res("BadBrush");
        _muted = Res("MutedBrush");
        _accent = Res("AccentBrush");
        _blue = Res("AccentBrush");

        foreach (var def in PidCatalog.All)
        {
            var pv = new PidValue(def);
            _pids[def.Pid] = pv;
            LivePids.Add(pv);
        }

        Rpm = _pids[0x0C];
        Coolant = _pids[0x05];
        Voltage = _pids[0x42];
        Throttle = _pids[0x11];
        Speed = _pids[0x0D];
        Load = _pids[0x04];
        IntakeTemp = _pids[0x0F];
        FuelLevel = _pids[0x2F];
        Maf = _pids[0x10];
        Timing = _pids[0x0E];
        InitTrends();

        ChartSeriesList = new List<ChartSeries>
        {
            new ChartSeries("Engine speed (rpm)", _blue, 0, 6000, ChartCapacity),
            new ChartSeries("Coolant temp (°C)", _good, 0, 120, ChartCapacity),
            new ChartSeries("Battery voltage (V)", _warn, 10, 16, ChartCapacity),
        };
        foreach (var s in ChartSeriesList)
            s.PropertyChanged += (_, _) => ChartUpdated?.Invoke();

        Protocols = new[]
        {
            new ProtocolOption(0, "Automatic (adapter decides)"),
            new ProtocolOption(6, "ISO 15765-4 CAN 11-bit, 500 kbaud (RAV4 2019+)"),
            new ProtocolOption(7, "ISO 15765-4 CAN 29-bit, 500 kbaud"),
            new ProtocolOption(3, "ISO 9141-2 (K-line)"),
            new ProtocolOption(5, "ISO 14230-4 KWP (fast init)"),
        };
        SelectedProtocol = Protocols[1];

        _loading = true;
        var saved = AppSettings.Load();
        if (Enum.TryParse<ConnectionMode>(saved.Mode, out var savedMode) && savedMode != ConnectionMode.Replay) _mode = savedMode;
        _wifiHost = saved.WifiHost;
        _wifiPort = saved.WifiPort;
        if (BaudRates.Contains(saved.Baud)) _selectedBaud = saved.Baud;
        SelectedProtocol = Protocols.FirstOrDefault(p => p.Number == saved.Protocol) ?? Protocols[1];
        _imperial = saved.Imperial;
        Units.Imperial = _imperial;
        _pollDelayMs = Math.Clamp(saved.PollDelayMs, 0, 500);
        _simulateFaults = saved.SimulateFaults;
        _savedPort = saved.Port;
        InitFeatures(saved);
        _loading = false;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null && PersistedNames.Contains(e.PropertyName))
                SaveSettings();
        };

        ConnectCommand = new RelayCommand(() => { CancelReconnect(); Fire(ConnectAsync()); }, () => !IsConnected && !IsBusy);
        DisconnectCommand = new RelayCommand(() => { CancelReconnect(); Fire(DisconnectAsync()); }, () => IsConnected || IsBusy);
        RefreshPortsCommand = new RelayCommand(StartPortScan);
        OpenBluetoothSettingsCommand = new RelayCommand(() => OpenUri("ms-settings:bluetooth"));
        RefreshDiagnosticsCommand = new RelayCommand(() => Fire(RefreshDiagnosticsAsync()), () => IsConnected);
        ToggleLoggingCommand = new RelayCommand(ToggleLogging);
        OpenLogsFolderCommand = new RelayCommand(() => OpenFolder(LogsFolder));
        OpenLogFileCommand = new RelayCommand(() => { if (SelectedLogFile is not null) OpenFile(SelectedLogFile.Path); },
                                              () => SelectedLogFile is not null);
        CreateReportCommand = new RelayCommand(CreateReport);
        OpenReportsFolderCommand = new RelayCommand(() => OpenFolder(ReportsFolder));
        OpenProjectsCommand = new RelayCommand(() => OpenFolder(AppFolder));
        ShowHelpCommand = new RelayCommand(ShowHelp);
        ShowDiagnosticsCommand = new RelayCommand(() => NavigationRequested?.Invoke("Diagnostics"));
        ShowSettingsCommand = new RelayCommand(() => NavigationRequested?.Invoke("Settings"));
        ShowAboutCommand = new RelayCommand(() =>
        {
            RefreshAbout();
            NavigationRequested?.Invoke("About");
        });
        CopyAboutCommand = new RelayCommand(() => CopyText(AboutInfo.ToText(AboutSections), "About details copied."));

        CopyVinCommand = new RelayCommand(() => CopyText(_vin?.Vin ?? "", "VIN copied."), () => _vin is not null);
        CopyIdentityCommand = new RelayCommand(() => CopyText(IdentitySummary, "Vehicle and ECU information copied."));
        RunAdapterTestCommand = new RelayCommand(() => Fire(RunAdapterTestAsync()), () => !IsConnected && !IsBusy && !IsTesting);
        OpenDtcCommand = new RelayCommand<DtcInfo>(OpenDtc);
        OpenCodesFolderCommand = new RelayCommand(OpenCodesFolder);
        ReloadCodesCommand = new RelayCommand(ReloadCodes);
        _dtcCategory = DtcCategories[0];
        DtcDatabaseSummary = "Loading codes...";
        Fire(LoadCodeDatabaseAsync());
        InitFeatureCommands();

        StartPortScan();
        RefreshLogFiles();
        RefreshReportFiles();
        LoadBaseline();
        ReloadCustomParameters();
        RaiseAll();
    }

    // ----- Events and commands -----

    /// <summary>Raised on every poll batch so the graph redraws.</summary>
    public event Action? ChartUpdated;

    /// <summary>Raised when a button asks to show a page ("Diagnostics", "Settings", ...).</summary>
    public event Action<string>? NavigationRequested;

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand RefreshPortsCommand { get; }
    public RelayCommand OpenBluetoothSettingsCommand { get; }
    public RelayCommand RefreshDiagnosticsCommand { get; }
    public RelayCommand ToggleLoggingCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand OpenLogFileCommand { get; }
    public RelayCommand CreateReportCommand { get; }
    public RelayCommand OpenReportsFolderCommand { get; }
    public RelayCommand OpenProjectsCommand { get; }
    public RelayCommand ShowHelpCommand { get; }
    public RelayCommand ShowDiagnosticsCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ShowAboutCommand { get; }
    public RelayCommand CopyAboutCommand { get; }
    public RelayCommand RunAdapterTestCommand { get; }
    public RelayCommand<DtcInfo> OpenDtcCommand { get; }
    public RelayCommand OpenCodesFolderCommand { get; }
    public RelayCommand ReloadCodesCommand { get; }
    public RelayCommand CopyVinCommand { get; }
    public RelayCommand CopyIdentityCommand { get; }

    // ----- OBD2 connection setup -----

    private ConnectionMode _mode = ConnectionMode.Serial;

    public ConnectionMode SelectedMode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                Raise(nameof(IsSerialMode));
                Raise(nameof(IsWifiMode));
                Raise(nameof(IsDemoMode));
                Raise(nameof(ConnectionHint));
            }
        }
    }

    public bool IsSerialMode { get => _mode == ConnectionMode.Serial; set { if (value) SelectedMode = ConnectionMode.Serial; } }
    public bool IsWifiMode { get => _mode == ConnectionMode.Wifi; set { if (value) SelectedMode = ConnectionMode.Wifi; } }
    public bool IsDemoMode { get => _mode == ConnectionMode.Demo; set { if (value) SelectedMode = ConnectionMode.Demo; } }

    public string ConnectionHint => _mode switch
    {
        ConnectionMode.Serial => "Bluetooth or USB ELM327. Bluetooth adapters appear as a COM port after pairing.",
        ConnectionMode.Wifi => "ELM327 Wi-Fi adapter. Join its network first, then enter its address (usually 192.168.0.10, port 35000).",
        _ => "Simulated RAV4 ECU. No hardware needed.",
    };

    public ObservableCollection<PortOption> Ports { get; } = new();

    private PortOption? _selectedPort;
    public PortOption? SelectedPort
    {
        get => _selectedPort;
        set => Set(ref _selectedPort, value);
    }

    private string _wifiHost = "192.168.0.10";
    public string WifiHost
    {
        get => _wifiHost;
        set => Set(ref _wifiHost, value);
    }

    private int _wifiPort = 35000;
    public int WifiPort
    {
        get => _wifiPort;
        set => Set(ref _wifiPort, value);
    }

    public IReadOnlyList<int> BaudRates { get; } = new[] { 9600, 38400, 115200, 230400, 500000 };

    private bool _simulateFaults;

    /// <summary>Demo mode only: the simulated ECU reports a stored (P0420) and a pending (P0301) code.</summary>
    public bool SimulateFaults
    {
        get => _simulateFaults;
        set => Set(ref _simulateFaults, value);
    }

    private int _selectedBaud = 38400;
    public int SelectedBaud
    {
        get => _selectedBaud;
        set => Set(ref _selectedBaud, value);
    }

    public IReadOnlyList<ProtocolOption> Protocols { get; }

    private ProtocolOption _selectedProtocol = null!;
    public ProtocolOption SelectedProtocol
    {
        get => _selectedProtocol;
        set => Set(ref _selectedProtocol, value);
    }

    public int PollDelayMs
    {
        get => _pollDelayMs;
        set => Set(ref _pollDelayMs, Math.Clamp(value, 0, 500));
    }

    // ----- Link and system status -----

    private LinkState _state = LinkState.Disconnected;
    public LinkState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                RaiseCommands();
                RaiseAll();
            }
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                RaiseCommands();
        }
    }

    public bool IsConnected => State is LinkState.Connected or LinkState.Simulated or LinkState.Replay;

    public bool IsDemo => State == LinkState.Simulated;

    private string _statusMessage = "Ready. Choose how to reach the adapter, then Connect.";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public string ConnectionText => State switch
    {
        LinkState.Connected => "Connected",
        LinkState.Simulated => "Connected (DEMO)",
        LinkState.Replay => "Connected (REPLAY)",
        LinkState.Connecting => "Connecting…",
        LinkState.Error => "Error",
        _ => "Disconnected",
    };

    public Brush ConnectionBrush => State switch
    {
        LinkState.Connected or LinkState.Simulated or LinkState.Replay => _good,
        LinkState.Connecting => _warn,
        LinkState.Error => _bad,
        _ => _muted,
    };

    public string SystemTitle => State switch
    {
        LinkState.Connected or LinkState.Simulated or LinkState.Replay => "System Connected",
        LinkState.Connecting => "Connecting…",
        LinkState.Error => "Connection Error",
        _ => "Not Connected",
    };

    public string UsbStatusText => IsConnected ? "Connected" : "Not connected";
    public Brush UsbStatusBrush => IsConnected ? _good : _muted;

    public string CanStatusText => IsConnected
        ? (_activeProtocol is 6 or 7 ? "Active (500 kbps)" : $"Protocol {_activeProtocol}")
        : "Inactive";
    public Brush CanStatusBrush => IsConnected ? _good : _muted;

    public string EcuStatusText => IsConnected && _obd is not null && _obd.SupportedPids.Count > 0 ? "Detected" : "Not detected";
    public Brush EcuStatusBrush => IsConnected && _obd is not null && _obd.SupportedPids.Count > 0 ? _good : _muted;

    public string LinkSpeedText => IsConnected
        ? (_activeProtocol is 6 or 7 ? "500 kbps" : "Serial link")
        : "Not connected";

    public string AdapterId { get; private set; } = "—";
    public string ProtocolText { get; private set; } = "—";
    public string SupportedPidsText { get; private set; } = "—";

    public string EcuSummary => _obd is null || _obd.SupportedPids.Count == 0
        ? "Not detected"
        : $"{_obd.SupportedPids.Count} PIDs supported";

    // ----- Vehicle -----

    public string VehicleTitle => $"{_vin?.ModelYear?.ToString() ?? "2021"} Toyota RAV4";
    public string VehicleEngineText => VehicleEngine;
    public string VehicleTransmissionText => VehicleTransmission;
    public string VinText => _vin?.Vin ?? "Not read yet";

    public string VinDetails => _vin is null
        ? "Connect to the ECU to read the VIN."
        : $"{_vin.Manufacturer} · {_vin.Region} · Check digit {(_vin.CheckDigitValid ? "valid" : "INVALID")}";

    // ----- Metrics page: units and session peaks -----

    private bool _imperial;
    private double _peakRpm, _peakSpeedKmh, _peakCoolantC, _minVolt = double.NaN, _maxVolt = double.NaN;

    public bool UseImperial
    {
        get => _imperial;
        set
        {
            if (Set(ref _imperial, value))
            {
                Units.Imperial = value;
                foreach (var pv in _pids.Values) pv.RefreshUnits();
                Raise(nameof(UnitsButtonText));
                Raise(nameof(UnitsShortText));
                Raise(nameof(IsMetric));
                Raise(nameof(IsImperial));
                RaiseMetrics();
                Raise(nameof(FreezeRows));
                ChartUpdated?.Invoke();
            }
        }
    }

    public RelayCommand ToggleUnitsCommand => new(() => UseImperial = !UseImperial);

    public string UnitsShortText => _imperial ? "Imperial: mph, °F, psi" : "Metric: km/h, °C, kPa";
    public bool IsMetric { get => !_imperial; set { if (value) UseImperial = false; } }
    public bool IsImperial { get => _imperial; set { if (value) UseImperial = true; } }

    public string UnitsButtonText => _imperial ? "Show metric (km/h, °C)" : "Show imperial (mph, °F)";

    private static double CtoF(double c) => c * 9 / 5 + 32;
    private double Spd(double kmh) => _imperial ? kmh * 0.621371 : kmh;
    private double Tmp(double c) => _imperial ? CtoF(c) : c;

    public double SpeedShown => Speed.HasValue ? Spd(Speed.Value) : double.NaN;
    public double SpeedGaugeMax => _imperial ? 125 : 200;
    public string SpeedUnitText => _imperial ? "mph" : "km/h";
    public double CoolantShown => Coolant.HasValue ? Tmp(Coolant.Value) : double.NaN;
    public double TempGaugeMin => _imperial ? 32 : 0;
    public double TempGaugeMax => _imperial ? 250 : 120;
    public string TempUnitText => _imperial ? "°F" : "°C";
    public string IntakeTempText => IntakeTemp.HasValue ? $"{Tmp(IntakeTemp.Value):0} {TempUnitText}" : "—";

    public string CoolantLegendText => $"Coolant Temp ({TempUnitText})";
    public string PeakRpmText => _peakRpm > 0 ? $"Peak {_peakRpm:0} rpm" : "Peak —";
    public string PeakSpeedText => _peakSpeedKmh > 0 ? $"Peak {Spd(_peakSpeedKmh):0} {SpeedUnitText}" : "Peak —";
    public string PeakCoolantText => _peakCoolantC > 0 ? $"Peak {Tmp(_peakCoolantC):0} {TempUnitText}" : "Peak —";
    public string VoltageRangeText => double.IsNaN(_minVolt) ? "Range —" : $"Range {_minVolt:0.0} to {_maxVolt:0.0} V";

    private void TrackPeaks()
    {
        if (Rpm.HasValue) _peakRpm = Math.Max(_peakRpm, Rpm.Value);
        if (Speed.HasValue) _peakSpeedKmh = Math.Max(_peakSpeedKmh, Speed.Value);
        if (Coolant.HasValue) _peakCoolantC = Math.Max(_peakCoolantC, Coolant.Value);
        if (Voltage.HasValue)
        {
            _minVolt = double.IsNaN(_minVolt) ? Voltage.Value : Math.Min(_minVolt, Voltage.Value);
            _maxVolt = double.IsNaN(_maxVolt) ? Voltage.Value : Math.Max(_maxVolt, Voltage.Value);
        }
    }

    private void RaiseMetrics()
    {
        foreach (var name in new[]
        {
            nameof(SpeedShown), nameof(SpeedGaugeMax), nameof(SpeedUnitText), nameof(CoolantShown), nameof(TempGaugeMin),
            nameof(TempGaugeMax), nameof(TempUnitText), nameof(IntakeTempText), nameof(PeakRpmText), nameof(PeakSpeedText),
            nameof(PeakCoolantText), nameof(VoltageRangeText), nameof(CoolantLegendText), nameof(FuelEconomyText), nameof(TripEconomyText),
            nameof(FuelTrimAssessment), nameof(LambdaAssessment), nameof(CatalystText),
        })
            Raise(name);
    }

    // ----- Live data -----

    public ObservableCollection<PidValue> LivePids { get; } = new();

    public PidValue Rpm { get; }
    public PidValue Coolant { get; }
    public PidValue Voltage { get; }
    public PidValue Throttle { get; }
    public PidValue Speed { get; }
    public PidValue Load { get; }
    public PidValue IntakeTemp { get; }
    public PidValue FuelLevel { get; }
    public PidValue Maf { get; }
    public PidValue Timing { get; }

    public IReadOnlyList<ChartSeries> ChartSeriesList { get; }

    public ChartSeries RpmSeries => ChartSeriesList[0];
    public ChartSeries CoolantSeries => ChartSeriesList[1];
    public ChartSeries VoltageSeries => ChartSeriesList[2];

    public IReadOnlyList<double> ChartTimes => _chartTimes;

    // ----- CAN-style monitor -----

    public ObservableCollection<CanRow> CanRows { get; } = new();

    // ----- Diagnostics -----

    public ObservableCollection<DtcInfo> StoredDtcs { get; } = new();
    public ObservableCollection<DtcInfo> PendingDtcs { get; } = new();
    public ObservableCollection<DtcInfo> ReadDtcs { get; } = new();
    public ObservableCollection<DtcInfo> PermanentDtcs { get; } = new();

    private FreezeFrameData? _freeze;
    public string FreezeTitle => _freeze is null ? "No freeze frame stored" : $"Freeze frame for {_freeze.Dtc}";
    public IEnumerable<FreezeValue> FreezeValues => _freeze?.Values ?? Enumerable.Empty<FreezeValue>();
    public IEnumerable<TestStepRow> FreezeRows =>
        (_freeze?.Values ?? Array.Empty<FreezeValue>()).Select(v => new TestStepRow(v.Name, "", Units.Format(v.Unit, v.Value, v.Format), _accent));

    private bool _showFreeze;
    public bool ShowFreezeFrame { get => _showFreeze; set { if (Set(ref _showFreeze, value)) Raise(nameof(ShowDatabase)); } }
    public bool ShowDatabase { get => !_showFreeze; set { if (value) ShowFreezeFrame = false; } }

    public bool HasDtcs => StoredDtcs.Count + PendingDtcs.Count > 0;

    public Brush DtcStateBrush => !_dtcsLoaded ? _muted : HasDtcs ? _warn : _good;

    public string DtcHeadline => !_dtcsLoaded
        ? "Not read yet"
        : HasDtcs ? $"{StoredDtcs.Count + PendingDtcs.Count} DTC(s) found" : "No DTCs Found";

    public string DtcSubtext => !_dtcsLoaded
        ? "Connect to the ECU to read fault codes."
        : HasDtcs ? "Read-only: codes are not cleared from this app." : "ECU reports no active fault codes";

    private bool _milOn;
    public bool MilOn
    {
        get => _milOn;
        private set
        {
            if (Set(ref _milOn, value)) Raise(nameof(MilText));
        }
    }

    public string MilText => !_readinessLoaded ? "—" : MilOn ? "MIL on" : "MIL off";

    private int _dtcCount;
    public int DtcCount
    {
        get => _dtcCount;
        private set => Set(ref _dtcCount, value);
    }

    private string _ignitionText = "—";
    public string IgnitionText
    {
        get => _ignitionText;
        private set => Set(ref _ignitionText, value);
    }

    private string _readinessHeadline = "Not read yet";
    public string ReadinessHeadline
    {
        get => _readinessHeadline;
        private set => Set(ref _readinessHeadline, value);
    }

    private string _readinessTimestamp = "";
    public string ReadinessTimestamp
    {
        get => _readinessTimestamp;
        private set => Set(ref _readinessTimestamp, value);
    }

    public ObservableCollection<MonitorRow> Monitors { get; } = new();

    // ----- Logging and reports -----

    private bool _isLogging;
    public bool IsLogging
    {
        get => _isLogging;
        private set
        {
            if (Set(ref _isLogging, value))
            {
                Raise(nameof(LoggingButtonText));
                Raise(nameof(LoggingStatusText));
                Raise(nameof(LoggingBrush));
            }
        }
    }

    public string LoggingButtonText => IsLogging ? "Stop Logging" : "Start Logging";
    public string LoggingStatusText => IsLogging ? "Logging" : "Not logging";

    public Brush LoggingBrush => IsLogging ? _bad : _muted;

    private string _logFilePath = "";
    public string LogFilePath
    {
        get => _logFilePath;
        private set => Set(ref _logFilePath, value);
    }

    private int _logRowCount;
    public int LogRowCount
    {
        get => _logRowCount;
        private set => Set(ref _logRowCount, value);
    }

    public ObservableCollection<FileItem> LogFiles { get; } = new();

    private FileItem? _selectedLogFile;
    public FileItem? SelectedLogFile
    {
        get => _selectedLogFile;
        set
        {
            if (Set(ref _selectedLogFile, value))
                OpenLogFileCommand.RaiseCanExecuteChanged();
        }
    }

    private string _lastReportPath = "";
    public string LastReportPath
    {
        get => _lastReportPath;
        private set => Set(ref _lastReportPath, value);
    }

    public ObservableCollection<FileItem> ReportFiles { get; } = new();

    public string AppFolderText => AppFolder;

    public static string AppFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RAV4 Control Studio");

    public static string LogsFolder => Path.Combine(VehicleFolder, "Logs");

    public static string CodesFolder => Path.Combine(AppFolder, "Codes");

    public static string ReportsFolder => Path.Combine(VehicleFolder, "Reports");

    // ----- DTC database -----

    public IReadOnlyList<CategoryOption> DtcCategories { get; } = new[]
    {
        new CategoryOption(null, "All codes"),
        new CategoryOption('P', "Powertrain (P)"),
        new CategoryOption('C', "Chassis (C)"),
        new CategoryOption('B', "Body (B)"),
        new CategoryOption('U', "Network (U)"),
    };

    private CategoryOption _dtcCategory = null!;
    public CategoryOption SelectedDtcCategory
    {
        get => _dtcCategory;
        set
        {
            if (Set(ref _dtcCategory, value))
                RunDtcSearch();
        }
    }

    private string _dtcSearch = "";
    public string DtcSearchText
    {
        get => _dtcSearch;
        set
        {
            if (Set(ref _dtcSearch, value))
                ScheduleDtcSearch();
        }
    }

    private DispatcherTimer? _dtcSearchTimer;

    /// <summary>Searches a quarter of a second after the last keystroke instead of on every keystroke.</summary>
    private void ScheduleDtcSearch()
    {
        _dtcSearchTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _dtcSearchTimer!.Stop();
            RunDtcSearch();
        }, _ui);
        _dtcSearchTimer.Stop();
        _dtcSearchTimer.Start();
    }

    private IReadOnlyList<DtcEntry> _dtcResults = Array.Empty<DtcEntry>();

    /// <summary>Search results. Replaced as a whole so the grid refreshes once, not once per row.</summary>
    public IReadOnlyList<DtcEntry> DtcResults
    {
        get => _dtcResults;
        private set => Set(ref _dtcResults, value);
    }

    private DtcEntry? _selectedDbEntry;
    public DtcEntry? SelectedDtcEntry
    {
        get => _selectedDbEntry;
        set
        {
            if (Set(ref _selectedDbEntry, value) && value is not null)
                DetailEntry = value;
        }
    }

    private DtcEntry? _detailEntry;

    /// <summary>The entry shown in the detail panel. Set by picking a row or by looking up a code the ECU reported.</summary>
    public DtcEntry? DetailEntry
    {
        get => _detailEntry;
        private set
        {
            if (Set(ref _detailEntry, value))
            {
                Raise(nameof(DtcDetailTitle));
                Raise(nameof(DtcDetailText));
            }
        }
    }

    public string DtcDetailTitle => DetailEntry is null ? "Select a code" : DetailEntry.Code;

    public string DtcDetailText
    {
        get
        {
            var e = DetailEntry;
            if (e is null)
                return "Pick a code from the list, or search by code or words (for example 'misfire' or 'P0420').";

            var sb = new StringBuilder();
            sb.AppendLine(e.Description);
            if (e.Explanation.Length > 0) sb.AppendLine().AppendLine(e.Explanation);
            sb.AppendLine().AppendLine(e.System);
            if (e.EmissionsRelevant) sb.AppendLine("Emissions-related: can affect an emissions test.");
            if (e.LimpModePossible) sb.AppendLine("Limp mode is possible with this fault.");
            if (e.Causes is { Count: > 0 })
            {
                sb.AppendLine().AppendLine("Common causes:");
                foreach (var c in e.Causes) sb.AppendLine("  - " + c);
            }
            if (e.Symptoms is { Count: > 0 })
            {
                sb.AppendLine().AppendLine("Typical symptoms:");
                foreach (var x in e.Symptoms) sb.AppendLine("  - " + x);
            }
            sb.AppendLine().Append("Source: " + e.Source);
            return sb.ToString().TrimEnd();
        }
    }

    public string DtcDatabaseSummary { get; private set; } = "";

    private DtcInfo? _selectedReadDtc;

    /// <summary>A code the ECU reported; selecting it jumps to its database entry.</summary>
    public DtcInfo? SelectedReadDtc
    {
        get => _selectedReadDtc;
        set
        {
            if (Set(ref _selectedReadDtc, value) && value is not null)
                LookupDtc(value.Code);
        }
    }

    /// <summary>Shows the database entry for a code (used when the user picks a code the ECU reported).</summary>
    public void LookupDtc(string code)
    {
        // Search straight away (not after the typing delay), because the matching row is picked just below.
        _dtcCategory = DtcCategories[0];
        Raise(nameof(SelectedDtcCategory));
        _dtcSearch = code;
        Raise(nameof(DtcSearchText));
        _dtcSearchTimer?.Stop();
        RunDtcSearch();

        var entry = DtcResults.FirstOrDefault(e => e.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        SelectedDtcEntry = entry;
        DetailEntry = entry ?? new DtcEntry(
            code.ToUpperInvariant(),
            DtcCatalog.Describe(code),
            DtcDatabase.SystemOf(code),
            "This code is not in the built-in database. Toyota-specific codes can be added with Import codes... on this page.",
            Source: "Not in database");
    }

    private void OpenDtc(DtcInfo? dtc)
    {
        if (dtc is null) return;
        NavigationRequested?.Invoke("Diagnostics");
        _selectedReadDtc = dtc;
        Raise(nameof(SelectedReadDtc));
        LookupDtc(dtc.Code);
    }

    private async Task LoadCodeDatabaseAsync()
    {
        await Task.Run(() =>
        {
            DtcDatabase.LoadUserCodes(CodesFolder);
            _ = DtcDatabase.All.Count; // force the built-in data to load now, not on the first keystroke
        });
        RunDtcSearch();
    }

    private void RunDtcSearch()
    {
        var found = DtcDatabase.Search(_dtcSearch, _dtcCategory?.Letter, 400, out int total);
        DtcResults = found.ToList();

        DtcDatabaseSummary = total > DtcResults.Count
            ? $"Showing {DtcResults.Count} of {total:N0} matches. Refine your search. ({DtcDatabase.All.Count:N0} codes in database)"
            : $"{total:N0} matches ({DtcDatabase.All.Count:N0} codes in database)";
        Raise(nameof(DtcDatabaseSummary));
    }

    // ----- Vehicle and ECU identity -----

    public string VinYearText => _vin?.ModelYear?.ToString() ?? "—";
    public string VinMakerText => _vin?.Manufacturer ?? "—";
    public string VinWmiText => VinPart(0, 3);
    public string VinDescriptorText => VinPart(3, 6);
    public string VinPlantText => VinPart(10, 1);
    public string VinSerialText => VinPart(11, 6);

    private string VinPart(int start, int length) =>
        _vin is { LengthValid: true } v && v.Vin.Length >= start + length ? v.Vin.Substring(start, length) : "—";

    public string CalibrationIdText { get; private set; } = "—";
    public string CvnText { get; private set; } = "—";
    public string EcuNameText { get; private set; } = "—";
    public string InfoTypesText { get; private set; } = "—";
    public string AdapterVersionText { get; private set; } = "—";
    public string AdapterDescriptionText { get; private set; } = "—";
    public string AdapterSerialText { get; private set; } = "—";
    public string AdapterVoltageText { get; private set; } = "—";

    public ObservableCollection<string> SupportedPidItems { get; } = new();

    private void ApplyIdentity(ObdService service)
    {
        var id = service.Identity;
        string Join(IEnumerable<string> items) => items.Any() ? string.Join(", ", items) : "Not reported";

        CalibrationIdText = id is null ? "Not reported" : Join(id.CalibrationIds);
        CvnText = id is null ? "Not reported" : Join(id.CalibrationVerificationNumbers);
        EcuNameText = id?.EcuName ?? "Not reported";
        InfoTypesText = id is null || id.SupportedInfoTypes.Count == 0
            ? "Not reported"
            : string.Join(", ", id.SupportedInfoTypes.Select(t => $"09 {t:X2}"));
        AdapterVersionText = id?.AdapterVersion ?? service.AdapterId;
        AdapterDescriptionText = id?.AdapterDescription ?? "Not reported";
        AdapterSerialText = id?.AdapterSerial ?? "Not reported";
        AdapterVoltageText = id?.AdapterVoltage ?? "Not reported";

        SupportedPidItems.Clear();
        foreach (var pid in service.SupportedPids.OrderBy(p => p))
        {
            var def = PidCatalog.Find(pid);
            SupportedPidItems.Add(def is null ? $"0x{pid:X2}" : $"0x{pid:X2}  {def.Name}");
        }

        foreach (var name in new[]
        {
            nameof(CalibrationIdText), nameof(CvnText), nameof(EcuNameText), nameof(InfoTypesText),
            nameof(AdapterVersionText), nameof(AdapterDescriptionText), nameof(AdapterSerialText), nameof(AdapterVoltageText),
        })
            Raise(name);
    }

    public string IdentitySummary =>
        string.Join(Environment.NewLine, new[]
        {
            $"Vehicle:        {VehicleTitle}",
            $"VIN:            {VinText}",
            $"Model year:     {VinYearText}",
            $"Manufacturer:   {VinMakerText}",
            $"WMI / VDS:      {VinWmiText} / {VinDescriptorText}",
            $"Plant code:     {VinPlantText}",
            $"Serial (VIN seq): {VinSerialText}",
            $"ECU name:       {EcuNameText}",
            $"Calibration ID: {CalibrationIdText}",
            $"CVN:            {CvnText}",
            $"Adapter:        {AdapterVersionText} / {AdapterDescriptionText}",
            $"Adapter serial: {AdapterSerialText}",
            $"Protocol:       {ProtocolText}",
        });

    // ----- About -----

    public string AboutVersion => AboutInfo.Version;

    public ObservableCollection<AboutSection> AboutSections { get; } = new();

    private AboutSection? _selectedAboutSection;
    public AboutSection? SelectedAboutSection
    {
        get => _selectedAboutSection;
        set => Set(ref _selectedAboutSection, value);
    }

    private void RefreshAbout()
    {
        string? keep = SelectedAboutSection?.Title;
        AboutSections.Clear();
        foreach (var section in AboutInfo.Collect())
            AboutSections.Add(section);
        SelectedAboutSection = AboutSections.FirstOrDefault(x => x.Title == keep) ?? AboutSections.FirstOrDefault();
    }

    // ----- Adapter test -----

    public ObservableCollection<TestStepRow> AdapterTestRows { get; } = new();

    private bool _isTesting;
    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (Set(ref _isTesting, value))
                RaiseCommands();
        }
    }

    private string _adapterTestSummary = "Checks the link, adapter, voltage, protocol and ECU. Disconnect first.";
    public string AdapterTestSummary
    {
        get => _adapterTestSummary;
        private set => Set(ref _adapterTestSummary, value);
    }

    private Brush? _adapterTestBrush;
    public Brush AdapterTestBrush
    {
        get => _adapterTestBrush ?? _muted;
        private set => Set(ref _adapterTestBrush, value);
    }

    public async Task RunAdapterTestAsync()
    {
        if (IsConnected || IsBusy || IsTesting) return;

        var mode = SelectedMode;
        var port = SelectedPort?.Name;
        var host = WifiHost.Trim();
        var wifiPort = WifiPort;
        var baud = SelectedBaud;
        var protocol = SelectedProtocol.Number;
        var faults = SimulateFaults;

        AdapterTestRows.Clear();
        if (mode == ConnectionMode.Serial && port is null)
        {
            AdapterTestSummary = "No serial adapter found. Pair a Bluetooth ELM327 or plug in USB, then press Refresh.";
            AdapterTestBrush = _bad;
            return;
        }

        IsTesting = true;
        AdapterTestSummary = "Testing the adapter...";
        AdapterTestBrush = _warn;

        try
        {
            var report = await Task.Run(() => AdapterTester.Run(
                MakeTransport(mode, port, host, wifiPort, baud, faults),
                protocol,
                step => _ui.InvokeAsync(() => AdapterTestRows.Add(ToRow(step)))));

            AdapterTestSummary = report.Summary;
            AdapterTestBrush = report.AllPassed ? _good : report.AdapterOk ? _warn : _bad;
            Notify("Adapter test finished: " + report.Summary);
        }
        catch (Exception ex)
        {
            AdapterTestSummary = "The test could not run: " + ex.Message;
            AdapterTestBrush = _bad;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private TestStepRow ToRow(AdapterTestStep step) => step.Result switch
    {
        StepResult.Passed => new TestStepRow(step.Name, step.Detail, "Passed", _good),
        StepResult.Failed => new TestStepRow(step.Name, step.Detail, "Failed", _bad),
        _ => new TestStepRow(step.Name, step.Detail, "Skipped", _muted),
    };

    private IElmTransport MakeTransport(ConnectionMode mode, string? port, string host, int wifiPort, int baud, bool faults) =>
        mode switch
        {
            ConnectionMode.Demo => new SimulatedElmTransport(faults),
            ConnectionMode.Replay => new LogReplayTransport(ReplayPath),
            ConnectionMode.Wifi => new TcpElmTransport(host, wifiPort),
            _ => new SerialElmTransport(port!, baud),
        };

    // ----- Actions: connection -----

    private Task? _portScan;

    private void StartPortScan()
    {
        if (_portScan is { IsCompleted: false }) return; // already scanning
        _portScan = RefreshPortsAsync();
        Fire(_portScan);
    }

    public async Task RefreshPortsAsync()
    {
        var previous = SelectedPort?.Name ?? _savedPort;
        var found = await Task.Run(PortScanner.Scan); // WMI can take a second; keep the window responsive
        Ports.Clear();
        foreach (var (name, label) in found)
            Ports.Add(new PortOption(name, label));

        SelectedPort = Ports.FirstOrDefault(p => p.Name == previous) ?? Ports.FirstOrDefault();

        Notify(Ports.Count == 0
            ? "No serial adapters found. Pair a Bluetooth ELM327 in Windows, plug in USB, or use Wi-Fi / Demo."
            : $"Found {Ports.Count} serial port(s).");
    }

    public async Task ConnectAsync()
    {
        if (IsConnected || IsBusy) return;

        // The port list loads in the background at start-up; a quick Connect press must wait for it,
        // or no port is selected yet. (The scan never throws; failures fall back to an empty list.)
        var scan = _portScan;
        if (scan is { IsCompleted: false })
        {
            Notify("Looking for serial ports...");
            try { await scan; } catch (Exception) { }
            if (IsConnected || IsBusy) return;
        }

        var mode = SelectedMode;
        var port = SelectedPort?.Name;
        var host = WifiHost.Trim();
        var wifiPort = WifiPort;
        var baud = SelectedBaud;
        var protocol = SelectedProtocol.Number;
        var faults = SimulateFaults;

        if (mode == ConnectionMode.Serial && port is null)
        {
            Notify("Choose an adapter port, or switch to Wi-Fi or Demo.");
            return;
        }
        if (mode == ConnectionMode.Wifi && (host.Length == 0 || wifiPort <= 0))
        {
            Notify("Enter the Wi-Fi adapter address and port.");
            return;
        }

        if (mode == ConnectionMode.Replay && !File.Exists(ReplayPath))
        {
            Notify("Choose a saved log on the Data Logging page, then press Replay.");
            RestoreModeAfterReplay();
            return;
        }

        IsBusy = true;
        State = LinkState.Connecting;
        Notify(mode switch
        {
            ConnectionMode.Demo => "Starting simulated ECU…",
            ConnectionMode.Replay => "Opening log replay…",
            ConnectionMode.Wifi => $"Connecting to {host}:{wifiPort}…",
            _ => $"Opening {port} at {baud} baud…",
        });

        try
        {
            var (service, snapshot) = await Task.Run(() => OpenSession(mode, port, host, wifiPort, baud, protocol, faults));

            _obd = service;
            _activeProtocol = protocol;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _sessionStart = DateTime.Now;
            while (_trafficQueue.TryDequeue(out _)) { }
            lock (_readGate) _pendingReadings.Clear();
            ResetTrip();
            foreach (var trend in TrendItems) trend.Series.Clear();
            UpdatePriority();
            _peakRpm = _peakSpeedKmh = _peakCoolantC = 0;
            _minVolt = _maxVolt = double.NaN;
            _chartTimes.Clear();
            foreach (var s in ChartSeriesList) s.Clear();
            CanRows.Clear();

            AdapterId = service.AdapterId;
            ProtocolText = service.Protocol;
            SetVin(service.Vin);
            ApplyIdentity(service);

            var supported = service.SupportedPids;
            SupportedPidsText = supported.Count == 0
                ? "No supported-PID data returned."
                : string.Join(", ", supported.OrderBy(p => p).Select(p => $"0x{p:X2}"));
            foreach (var pv in _pids.Values)
                pv.IsSupported = supported.Count == 0 || supported.Contains(pv.Pid);

            ApplySnapshot(snapshot);

            State = mode == ConnectionMode.Demo ? LinkState.Simulated : mode == ConnectionMode.Replay ? LinkState.Replay : LinkState.Connected;
            Raise(nameof(AdapterId));
            Raise(nameof(ProtocolText));
            Raise(nameof(SupportedPidsText));

            StartUiTimer();
            _pollTask = Task.Run(() => PollLoop(service, token));
            Notify(mode == ConnectionMode.Demo
                ? "Simulated ECU connected (DEMO MODE)."
                : $"Connected to the ECU through {service.TransportName}.");
        }
        catch (Exception ex)
        {
            State = LinkState.Error;
            Notify($"Connection failed: {ex.Message}");
            RestoreModeAfterReplay();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (!IsConnected && !IsBusy) return;

        _scanCts?.Cancel(); // a running ECU scan holds the adapter; stop it so the link can close
        _cts?.Cancel();
        var poll = _pollTask;
        if (poll is not null)
            await Task.Run(() => { try { poll.Wait(3000); } catch (AggregateException) { } });

        var obd = _obd;
        _obd = null;
        if (obd is not null)
            await Task.Run(obd.Dispose);

        _cts?.Dispose();
        _cts = null;
        _pollTask = null;

        BuildTripSummary();
        StopUiTimer();
        StopLogging();

        foreach (var pv in _pids.Values)
            pv.Value = double.NaN;

        RaiseMetrics();
        State = LinkState.Disconnected;
        IsBusy = false;
        Notify("Disconnected.");
        RestoreModeAfterReplay();
    }

    public async Task RefreshDiagnosticsAsync()
    {
        var obd = _obd;
        if (obd is null)
        {
            Notify("Connect to the ECU first.");
            return;
        }

        try
        {
            var snapshot = await Task.Run(() => new Snapshot(
                obd.ReadStoredDtcs(), obd.ReadPendingDtcs(), obd.ReadReadiness(),
                SafePermanent(obd), SafeFreeze(obd)));
            ApplySnapshot(snapshot);
            Notify($"Diagnostics refreshed: {DtcHeadline}.");
        }
        catch (Exception ex)
        {
            Notify($"Could not read diagnostics: {ex.Message}");
        }
    }

    // ----- Actions: logging and reports -----

    public void ToggleLogging()
    {
        if (IsLogging)
        {
            StopLogging();
            return;
        }

        if (!IsConnected)
        {
            Notify("Connect to the ECU before starting a log.");
            return;
        }

        var path = Path.Combine(LogsFolder, $"log-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        _logger = new CsvLogger(path, LivePids.Where(p => p.IsSupported).ToList());
        LogFilePath = path;
        LogRowCount = 0;
        IsLogging = true;
        Notify($"Logging live data to {path}");
    }

    public void CreateReport()
    {
        try
        {
            Directory.CreateDirectory(ReportsFolder);
            var path = Path.Combine(ReportsFolder, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, BuildReport(), new UTF8Encoding(false));
            LastReportPath = path;
            RefreshReportFiles();
            Notify($"Report saved: {path}");
        }
        catch (Exception ex)
        {
            Notify($"Could not save report: {ex.Message}");
        }
    }

    public void RefreshLogFiles() => FillFiles(LogFiles, LogsFolder, "*.csv");

    public void RefreshReportFiles() => FillFiles(ReportFiles, ReportsFolder, "*.txt", "*.pdf");

    public void Dispose()
    {
        // Synchronous shutdown for app exit (must not wait on the UI thread).
        _uiTimer?.Stop();
        FlushSettings();
        _remote?.Dispose();
        _reconnectCts?.Cancel();
        _scanCts?.Cancel();
        _cts?.Cancel();
        try { _pollTask?.Wait(2000); } catch (AggregateException) { }
        _obd?.Dispose();
        _obd = null;
        StopLogging();
    }

    // ----- Background work -----

    private (ObdService Service, Snapshot Snapshot) OpenSession(
        ConnectionMode mode, string? port, string host, int wifiPort, int baud, int protocol, bool faults)
    {
        IElmTransport transport = MakeTransport(mode, port, host, wifiPort, baud, faults);

        ObdService? service = null;
        try
        {
            transport.Open();
            service = new ObdService(transport);
            service.Traffic += entry =>
            {
                _trafficQueue.Enqueue(entry);
                while (_trafficQueue.Count > 100) _trafficQueue.TryDequeue(out _);
            };

            service.Initialize(protocol);
            service.LoadEcuInfo();

            var snapshot = new Snapshot(
                service.ReadStoredDtcs(),
                service.ReadPendingDtcs(),
                service.ReadReadiness(),
                SafePermanent(service),
                SafeFreeze(service));

            return (service, snapshot);
        }
        catch
        {
            if (service is not null) service.Dispose();
            else transport.Dispose();
            throw;
        }
    }

    // Permanent codes and freeze frames are optional on many ECUs: a timeout there must not fail the connection.
    private static IReadOnlyList<DtcInfo> SafePermanent(ObdService o)
    {
        try { return o.ReadPermanentDtcs(); }
        catch (TimeoutException) { return Array.Empty<DtcInfo>(); }
    }

    private static FreezeFrameData? SafeFreeze(ObdService o)
    {
        try { return o.ReadFreezeFrame(); }
        catch (TimeoutException) { return null; }
    }

    private void PollLoop(ObdService obd, CancellationToken ct)
    {
        var supported = PidCatalog.All
            .Where(p => obd.SupportedPids.Count == 0 || obd.SupportedPids.Contains(p.Pid))
            .ToList();
        var dashboard = supported.Where(p => PidCatalog.DashboardPids.Contains(p.Pid)).ToList();
        var rest = supported.Where(p => !PidCatalog.DashboardPids.Contains(p.Pid)).ToList();

        int timeouts = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Dashboard PIDs every cycle, plus two rotating extras so the full list stays fresh.
                var batch = new List<PidDefinition>(dashboard);
                for (int i = 0; i < 2 && rest.Count > 0; i++)
                    batch.Add(rest[_pollRotation++ % rest.Count]);

                // PIDs the visible page needs (Trends or Fuel and O2) are read every cycle.
                foreach (var pid in _priorityPids)
                {
                    var def = PidCatalog.Find(pid);
                    if (def is not null && !batch.Contains(def) && (obd.SupportedPids.Count == 0 || obd.SupportedPids.Contains(pid)))
                        batch.Add(def);
                }

                var readings = new Dictionary<byte, double>();
                foreach (var def in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var value = obd.ReadPid(def);
                        if (value.HasValue) readings[def.Pid] = value.Value;
                        timeouts = 0;
                    }
                    catch (TimeoutException)
                    {
                        if (++timeouts >= 8)
                            throw new IOException("The adapter stopped responding.");
                    }
                }

                lock (_readGate)
                {
                    foreach (var (pid, value) in readings)
                        _pendingReadings[pid] = value;
                }
                PollCustom(obd);
                Thread.Sleep(PollDelayMs);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Only a failure during normal polling is a lost link. Once Disconnect has cancelled the loop, errors
            // from the adapter being closed underneath it are expected and must not start an auto-reconnect.
            _ui.InvokeAsync(() =>
            {
                if (!ct.IsCancellationRequested) OnLinkLost(ex.Message);
            });
        }
        catch (Exception)
        {
            // shutdown in progress
        }
    }

    // ----- UI-thread updates -----

    private void StartUiTimer()
    {
        _uiTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => FlushUi(), _ui);
        _uiTimer.Start();
    }

    private void StopUiTimer()
    {
        _uiTimer?.Stop();
        lock (_readGate) _pendingReadings.Clear();
        while (_trafficQueue.TryDequeue(out _)) { }
    }

    /// <summary>Applies buffered readings and traffic on the UI thread (called every 200 ms while connected).</summary>
    private void FlushUi()
    {
        if (!IsConnected) return;

        Dictionary<byte, double> readings;
        lock (_readGate)
        {
            readings = new Dictionary<byte, double>(_pendingReadings);
            _pendingReadings.Clear();
        }
        ApplyReadings(readings);

        // Only the newest few requests are visible in the CAN table, so older ones are dropped.
        var recent = new List<TrafficEntry>();
        while (_trafficQueue.TryDequeue(out var entry)) recent.Add(entry);
        foreach (var entry in recent.Skip(Math.Max(0, recent.Count - 8)))
            AddCanRows(entry);
    }

    private void ApplyReadings(IReadOnlyDictionary<byte, double> readings)
    {
        if (!IsConnected) return;

        foreach (var (pid, value) in readings)
        {
            if (_pids.TryGetValue(pid, out var pv))
                pv.Value = value;
        }

        TrackPeaks();
        RaiseMetrics();
        TickFeatures();

        double seconds = (DateTime.Now - _sessionStart).TotalSeconds;
        _chartTimes.Add(seconds);
        if (_chartTimes.Count > ChartCapacity) _chartTimes.RemoveAt(0);

        ChartSeriesList[0].Add(Rpm.HasValue ? Rpm.Value : double.NaN);
        ChartSeriesList[1].Add(Coolant.HasValue ? Coolant.Value : double.NaN);
        ChartSeriesList[2].Add(Voltage.HasValue ? Voltage.Value : double.NaN);
        ChartUpdated?.Invoke();

        if (_logger is not null)
        {
            _logger.WriteRow(DateTime.Now);
            LogRowCount = _logger.RowCount;
        }
    }

    private void ApplySnapshot(Snapshot snapshot)
    {
        StoredDtcs.Clear();
        foreach (var dtc in snapshot.Stored) StoredDtcs.Add(dtc);

        PendingDtcs.Clear();
        foreach (var dtc in snapshot.Pending) PendingDtcs.Add(dtc);

        ReadDtcs.Clear();
        foreach (var dtc in snapshot.Stored.Concat(snapshot.Pending).Concat(snapshot.Permanent)) ReadDtcs.Add(dtc);
        PermanentDtcs.Clear();
        foreach (var dtc in snapshot.Permanent) PermanentDtcs.Add(dtc);
        _freeze = snapshot.Freeze;
        Raise(nameof(FreezeTitle));
        Raise(nameof(FreezeRows));

        _dtcsLoaded = true;
        Raise(nameof(HasDtcs));
        Raise(nameof(DtcStateBrush));
        Raise(nameof(DtcHeadline));
        Raise(nameof(DtcSubtext));

        if (snapshot.Readiness is { } r)
        {
            _readinessLoaded = true;
            MilOn = r.MilOn;
            DtcCount = r.DtcCount;
            IgnitionText = r.CompressionIgnition ? "Compression (diesel)" : "Spark (gasoline)";
            Raise(nameof(MilText));

            Monitors.Clear();
            foreach (var m in r.Monitors.Where(m => m.Supported))
            {
                Monitors.Add(m.Complete
                    ? new MonitorRow(m.Name, "Ready", _good)
                    : new MonitorRow(m.Name, "Not ready", _warn));
            }

            ReadinessHeadline = r.AllSupportedComplete ? "Ready for inspection" : "Not ready. Monitors still running.";
            ReadinessTimestamp = $"Checked {r.Timestamp:HH:mm:ss}";
        }
    }

    private void SetVin(string? vin)
    {
        _vin = vin is null ? null : VinDecoder.Decode(vin);
        Raise(nameof(VehicleTitle));
        Raise(nameof(VinText));
        Raise(nameof(VinDetails));
        foreach (var name in new[]
        {
            nameof(VinYearText), nameof(VinMakerText), nameof(VinWmiText), nameof(VinDescriptorText),
            nameof(VinPlantText), nameof(VinSerialText),
        })
            Raise(name);
        CopyVinCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Turns one request/response into two CAN-style rows: the request and the reply.</summary>
    private void AddCanRows(TrafficEntry entry)
    {
        string time = ((entry.Time - _sessionStart).TotalMilliseconds).ToString("#,##0.000");

        string requestData = FrameData(entry.Request);
        CanRows.Insert(0, new CanRow(time, "0x7DF", 8, requestData, true));

        string firstLine = entry.Response.Split(" | ")[0].Trim();
        string replyHex = firstLine.Replace(" ", "");
        bool isHex = replyHex.Length > 0 && replyHex.Length % 2 == 0 && replyHex.All(Uri.IsHexDigit);
        string replyData = isHex ? FrameData(replyHex) : firstLine.Length > 0 ? firstLine : "—";
        CanRows.Insert(1, new CanRow(time, "0x7E8", 8, replyData, false));

        while (CanRows.Count > CanCapacity)
            CanRows.RemoveAt(CanRows.Count - 1);
    }

    /// <summary>Formats an ISO-TP single frame: length byte, data bytes, zero padding to 8 bytes.</summary>
    private static string FrameData(string hex)
    {
        var clean = hex.Replace(" ", "").ToUpperInvariant();
        int count = clean.Length / 2;
        var sb = new StringBuilder();
        sb.Append($"{count:X2}");
        for (int i = 0; i < count && i < 7; i++)
            sb.Append(' ').Append(clean.Substring(i * 2, 2));
        for (int i = count; i < 7; i++)
            sb.Append(" 00");
        return sb.ToString();
    }

    private void OnLinkLost(string reason)
    {
        Fire(HandleLinkLostAsync(reason));
    }

    private void StopLogging()
    {
        if (_logger is null) return;

        int rows = _logger.RowCount;
        string path = _logger.FilePath;
        _logger.Dispose();
        _logger = null;
        IsLogging = false;
        RefreshLogFiles();
        Notify($"Logging stopped. {rows} rows saved to {path}");
    }

    private string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("RAV4 Control Studio - Diagnostic report");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("Mode: read-only (no commands that change the vehicle are sent)");
        sb.AppendLine("Units: " + UnitsShortText);
        sb.AppendLine();
        sb.AppendLine("VEHICLE");
        sb.AppendLine($"  Name:         {VehicleTitle}");
        sb.AppendLine($"  Engine:       {VehicleEngine}, transmission {VehicleTransmission}");
        sb.AppendLine($"  VIN:          {VinText}");
        sb.AppendLine($"  Details:      {VinDetails}");
        sb.AppendLine($"  Adapter:      {AdapterId}");
        sb.AppendLine($"  Protocol:     {ProtocolText}");
        sb.AppendLine();
        if (LastTripText.Length > 0)
        {
            sb.AppendLine("LAST TRIP");
            foreach (var tripLine in LastTripText.Split('\n')) sb.AppendLine("  " + tripLine.TrimEnd('\r'));
            sb.AppendLine();
        }
        sb.AppendLine("READINESS (I/M MONITORS)");
        sb.AppendLine($"  {MilText}, stored DTC count: {DtcCount}, ignition: {IgnitionText}");
        sb.AppendLine($"  {ReadinessHeadline} {ReadinessTimestamp}");
        foreach (var m in Monitors)
            sb.AppendLine($"    {m.Name,-28} {m.StatusText}");
        sb.AppendLine();
        sb.AppendLine("DIAGNOSTIC TROUBLE CODES");
        if (!_dtcsLoaded) sb.AppendLine("  Not read.");
        else if (!HasDtcs) sb.AppendLine("  No DTCs found.");
        foreach (var d in StoredDtcs) sb.AppendLine($"  [stored]  {d.Code}  {d.Description}");
        foreach (var d in PendingDtcs) sb.AppendLine($"  [pending] {d.Code}  {d.Description}");
        foreach (var d in PermanentDtcs) sb.AppendLine($"  [permanent] {d.Code}  {d.Description}");
        if (_freeze is not null)
        {
            sb.AppendLine().AppendLine($"FREEZE FRAME ({_freeze.Dtc})");
            foreach (var v in _freeze.Values) sb.AppendLine($"  {v.Name,-28} {Units.Format(v.Unit, v.Value, v.Format)}");
        }
        sb.AppendLine();
        sb.AppendLine("LIVE VALUES (latest reading)");
        foreach (var pv in LivePids)
            sb.AppendLine($"  {pv.Name,-28} {(pv.IsSupported ? pv.Display : "not supported")}");
        sb.AppendLine();
        sb.AppendLine("This report is for information only. It does not replace a diagnosis by a qualified technician.");
        return sb.ToString();
    }

    private void ShowHelp()
    {
        MessageBox.Show(
            "Quick start\n\n" +
            "1. OBD2 Connection: choose Bluetooth/USB (COM port), Wi-Fi (IP and port) or Demo.\n" +
            "2. Click Connect ECU. The app reads the VIN, supported PIDs, codes and readiness.\n" +
            "3. Live Data, Diagnostics, Data Logging, Tests and Reports work while connected.\n\n" +
            "The app is read-only. It never clears codes or controls the vehicle.",
            "RAV4 Control Studio - Help",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void FillFiles(ObservableCollection<FileItem> target, string folder, params string[] patterns)
    {
        target.Clear();
        if (!Directory.Exists(folder)) return;

        foreach (var file in patterns.SelectMany(p => new DirectoryInfo(folder).GetFiles(p)).OrderByDescending(f => f.LastWriteTime))
        {
            target.Add(new FileItem(
                file.Name,
                file.FullName,
                $"{file.LastWriteTime:yyyy-MM-dd HH:mm} · {file.Length / 1024.0:0.0} KB"));
        }
    }

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        OpenUri(folder);
    }

    private static void OpenFile(string path) => OpenUri(path);

    private static void OpenUri(string target) =>
        Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });

    private void OpenCodesFolder()
    {
        Directory.CreateDirectory(CodesFolder);
        string readme = Path.Combine(CodesFolder, "README.txt");
        if (!File.Exists(readme))
        {
            File.WriteAllText(readme,
                "Add your own trouble code lists here, then press Reload in the Diagnostics page." + Environment.NewLine + Environment.NewLine +
                "Supported files:" + Environment.NewLine +
                "  .json  an array like [ { \"dtc\": \"P1349\", \"description\": \"VVT system malfunction (Bank 1)\" } ]" + Environment.NewLine +
                "  .csv   one code per line: P1349,VVT system malfunction (Bank 1)" + Environment.NewLine + Environment.NewLine +
                "Codes you add override the built-in entry with the same code." + Environment.NewLine +
                "Only add lists you are allowed to use (for example data you own or that has an open licence).");
        }
        OpenFolder(CodesFolder);
    }

    private void ReloadCodes()
    {
        int count = DtcDatabase.LoadUserCodes(CodesFolder);
        RunDtcSearch();
        SelectedDtcEntry = null;
        DetailEntry = null;
        Notify(count == 0
            ? $"No imported codes found in {CodesFolder}."
            : $"Imported {count:N0} codes from {CodesFolder}.");
    }

    private void CopyText(string text, string message)
    {
        try
        {
            Clipboard.SetText(text);
            Notify(message);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Notify("The clipboard is busy. Try again.");
        }
    }

    private DispatcherTimer? _saveTimer;
    private static readonly object SaveGate = new();

    /// <summary>
    /// Saves settings half a second after the last change, on a background thread, so typing an address does not
    /// write the file on every keystroke.
    /// </summary>
    private void SaveSettings()
    {
        if (_loading) return;
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            var settings = BuildSettings();
            Task.Run(() => { lock (SaveGate) settings.Save(); });
        }, _ui);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes any pending settings change straight away (used at exit).</summary>
    private void FlushSettings()
    {
        if (_saveTimer is not { IsEnabled: true }) return;
        _saveTimer.Stop();
        var settings = BuildSettings();
        lock (SaveGate) settings.Save();
    }

    private AppSettings BuildSettings()
    {
        var settings = new AppSettings
        {
            Mode = (SelectedMode == ConnectionMode.Replay ? _modeBeforeReplay : SelectedMode).ToString(),
            WifiHost = WifiHost,
            WifiPort = WifiPort,
            Baud = SelectedBaud,
            Protocol = SelectedProtocol.Number,
            Port = SelectedPort?.Name,
            Imperial = UseImperial,
            PollDelayMs = PollDelayMs,
            SimulateFaults = SimulateFaults,
        }
        ;
        AddFeatureSettings(settings);
        return settings;
    }

    private void Notify(string message) => StatusMessage = message;

    /// <summary>
    /// Starts a task from a button or event without awaiting it, but still reports a failure in the status bar
    /// instead of losing the exception.
    /// </summary>
    private async void Fire(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notify("Something went wrong: " + ex.Message); }
    }

    private void RaiseCommands()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        RefreshDiagnosticsCommand.RaiseCanExecuteChanged();
        RunAdapterTestCommand.RaiseCanExecuteChanged();
        ScanEcusCommand?.RaiseCanExecuteChanged();
        SaveBaselineCommand?.RaiseCanExecuteChanged();
        ReplayLogCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>Empty property name tells WPF to refresh every binding on this view model.</summary>
    private void RaiseAll() => Raise(string.Empty);

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
