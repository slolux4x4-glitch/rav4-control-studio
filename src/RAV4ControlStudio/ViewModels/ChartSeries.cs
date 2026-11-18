using System.Windows.Media;

namespace RAV4ControlStudio.ViewModels;

/// <summary>Rolling window of samples for one graph line. NaN values are drawn as gaps.</summary>
public sealed class ChartSeries : ObservableObject
{
    private readonly List<double> _values = new();
    private bool _isVisible = true;

    public ChartSeries(string name, Brush brush, double min, double max, int capacity)
    {
        Name = name;
        Brush = brush;
        Min = min;
        Max = max;
        Capacity = capacity;
    }

    public string Name { get; }

    public Brush Brush { get; }

    /// <summary>Axis range shown on the graph.</summary>
    public double Min { get; }

    public double Max { get; }

    public int Capacity { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    public IReadOnlyList<double> Values => _values;

    public void Add(double value)
    {
        _values.Add(value);
        if (_values.Count > Capacity)
            _values.RemoveAt(0);
    }

    public void Clear() => _values.Clear();
}
