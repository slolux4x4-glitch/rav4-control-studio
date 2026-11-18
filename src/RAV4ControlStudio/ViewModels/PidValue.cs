using Obd2.Core.Obd;

namespace RAV4ControlStudio.ViewModels;

/// <summary>A live Mode 01 parameter with its latest decoded value. NaN means "no reading yet".</summary>
public sealed class PidValue : ObservableObject
{
    private double _value = double.NaN;
    private bool _supported = true;

    public PidValue(PidDefinition definition) => Definition = definition;

    public PidDefinition Definition { get; }

    public byte Pid => Definition.Pid;

    public double Min => Definition.Min;

    public double Max => Definition.Max;

    public string Name => Definition.Name;

    public string Group => Definition.Group;

    public string Unit => Definition.Unit;

    public string PidHex => $"0x{Definition.Pid:X2}";

    public double Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value))
            {
                Raise(nameof(HasValue));
                Raise(nameof(Display));
                Raise(nameof(BaselineText));
            }
        }
    }

    private double _baseline = double.NaN;

    /// <summary>A saved reference value (NaN when none) used to show how far the live value has moved.</summary>
    public double Baseline
    {
        get => _baseline;
        set
        {
            if (Set(ref _baseline, value))
                Raise(nameof(BaselineText));
        }
    }

    public string BaselineText
    {
        get
        {
            if (double.IsNaN(_baseline)) return "";
            string b = Units.Format(Definition.Unit, _baseline, Definition.Format);
            return HasValue
                ? $"Baseline {b}   ({Units.FormatDelta(Definition.Unit, _value - _baseline, Definition.Format)})"
                : $"Baseline {b}";
        }
    }

    public bool HasValue => !double.IsNaN(_value);

    public bool IsSupported
    {
        get => _supported;
        set
        {
            if (Set(ref _supported, value))
                Raise(nameof(SupportText));
        }
    }

    public string SupportText => IsSupported ? "Supported" : "Not supported by ECU";

    public string Display => HasValue ? Units.Format(Definition.Unit, _value, Definition.Format) : "—";

    /// <summary>Call after the metric / imperial preference changes.</summary>
    public void RefreshUnits()
    {
        Raise(nameof(Display));
        Raise(nameof(BaselineText));
    }
}
