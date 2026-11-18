namespace Obd2.Core.Obd;

public enum DtcStatus
{
    Stored,   // Mode 03: confirmed fault codes (MIL-related)
    Pending,  // Mode 07: detected on the current or last drive cycle, not yet confirmed
    Permanent, // Mode 0A: permanent codes that only clear after the monitor passes
}

public sealed record DtcInfo(string Code, string Description, DtcStatus Status);

public sealed record MonitorStatus(string Name, bool Supported, bool Complete);

public sealed record ReadinessStatus(
    bool MilOn,
    int DtcCount,
    bool CompressionIgnition,
    IReadOnlyList<MonitorStatus> Monitors,
    DateTime Timestamp)
{
    /// <summary>True when every supported monitor has completed (vehicle is "ready" for inspection).</summary>
    public bool AllSupportedComplete => Monitors.Where(m => m.Supported).All(m => m.Complete);
}

public sealed record VinInfo(
    string Vin,
    bool LengthValid,
    bool CheckDigitValid,
    int? ModelYear,
    string Manufacturer,
    string Region);

/// <summary>One request/response pair exchanged with the adapter.</summary>
public sealed record TrafficEntry(DateTime Time, string Request, string Response, bool IsError);

/// <summary>
/// Identity data. OBD-II has no standard ECU serial number, so the vehicle serial is the VIN's production
/// sequence (last six characters) and the ECU is identified by its calibration ID, CVN and name (Mode 09).
/// </summary>
public sealed record EcuIdentity(
    IReadOnlyList<string> CalibrationIds,
    IReadOnlyList<string> CalibrationVerificationNumbers,
    string? EcuName,
    IReadOnlyList<byte> SupportedInfoTypes,
    string AdapterVersion,
    string AdapterDescription,
    string AdapterSerial,
    string AdapterVoltage);

public sealed record FreezeValue(string Name, string Unit, double Value, string Format);

/// <summary>Snapshot of sensor values stored by the ECU when a fault code was set (Mode 02, frame 0).</summary>
public sealed record FreezeFrameData(string Dtc, IReadOnlyList<FreezeValue> Values);

/// <summary>An ECU that answered the discovery scan, with the identification fields it reported.</summary>
public sealed record EcuNode(int RequestId, int ResponseId, IReadOnlyDictionary<string, string> Fields)
{
    public string RequestHex => RequestId.ToString("X3");
    public string ResponseHex => ResponseId.ToString("X3");
}
