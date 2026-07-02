using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IxxatCanTool.ViewModels;

/// <summary>
/// One plottable signal time-series extracted from a loaded log: its sample
/// times (X, seconds) and physical values (Y), plus a checkbox-bound selection
/// flag the graph view watches to decide which lines to draw.
/// </summary>
public sealed class PlotSignal : INotifyPropertyChanged
{
    private bool _isSelected;

    public PlotSignal(string messageName, string signalName, string unit, double[] times, double[] values)
    {
        MessageName = messageName;
        SignalName = signalName;
        Unit = unit;
        Times = times;
        Values = values;
    }

    public string MessageName { get; }
    public string SignalName { get; }
    public string Unit { get; }
    public double[] Times { get; }
    public double[] Values { get; }

    public string Key => $"{MessageName}{SignalName}";

    /// <summary>Legend / list label, e.g. "EEC1 · EngineSpeed [rpm]".</summary>
    public string Label =>
        (string.IsNullOrEmpty(MessageName) ? SignalName : $"{MessageName} · {SignalName}")
        + (string.IsNullOrEmpty(Unit) ? "" : $" [{Unit}]");

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
