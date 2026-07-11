using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace IxxatCanTool.ViewModels;

/// <summary>
/// One signal shown as a live gauge card. The value is updated as frames arrive
/// (from the bus or from playback). The bar position (<see cref="Fraction"/>) is
/// scaled to the DBC [min,max]; if the DBC gives no range, it auto-expands to the
/// observed range so the bar still means something.
/// </summary>
public sealed class LiveSignal : INotifyPropertyChanged
{
    private readonly bool _fixedRange;
    private double? _value;
    private double _min;
    private double _max;
    private bool _isSelected = true;

    public LiveSignal(string messageName, string signalName, string unit, double min, double max)
    {
        MessageName = messageName;
        SignalName = signalName;
        Unit = unit;
        _fixedRange = max > min;
        _min = _fixedRange ? min : 0;
        _max = _fixedRange ? max : 0;
    }

    public string MessageName { get; }
    public string SignalName { get; }
    public string Unit { get; }

    /// <summary>
    /// Whether this signal is shown as a gauge when its CAN ID is enabled. Lets the user
    /// deselect individual signals of an ID. Raises <see cref="SelectionChanged"/> — a
    /// dedicated event so toggling it rebuilds the gauge list without putting the frequent
    /// value-update notifications on that path.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Raised only when <see cref="IsSelected"/> changes (not on value updates).</summary>
    public event Action? SelectionChanged;

    public bool HasValue => _value.HasValue;

    public string ValueText => _value is double v
        ? v.ToString("0.###", CultureInfo.InvariantCulture)
        : "—";

    // Blank the range labels until there's something to anchor them to (auto-range only).
    public string MinText => _fixedRange || _value is not null
        ? _min.ToString("0.#", CultureInfo.InvariantCulture) : "";
    public string MaxText => _fixedRange || _value is not null
        ? _max.ToString("0.#", CultureInfo.InvariantCulture) : "";

    /// <summary>Value position within [min,max], clamped to 0..1, for the bar.</summary>
    public double Fraction
    {
        get
        {
            if (_value is not double v || _max <= _min)
                return 0;
            double f = (v - _min) / (_max - _min);
            return f < 0 ? 0 : f > 1 ? 1 : f;
        }
    }

    /// <summary>Set the latest physical value; expands the range when the DBC gave none.</summary>
    public void Update(double value)
    {
        bool rangeChanged = false;
        if (!_fixedRange)
        {
            if (_value is null) { _min = value; _max = value; rangeChanged = true; }
            else
            {
                if (value < _min) { _min = value; rangeChanged = true; }
                if (value > _max) { _max = value; rangeChanged = true; }
            }
        }

        if (_value is double cur && cur.Equals(value) && !rangeChanged)
            return;

        bool firstValue = _value is null;
        _value = value;
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(Fraction));
        if (firstValue)
            OnPropertyChanged(nameof(HasValue));
        if (rangeChanged)
        {
            OnPropertyChanged(nameof(MinText));
            OnPropertyChanged(nameof(MaxText));
        }
    }

    /// <summary>
    /// Clear the displayed value back to "no data" (blank card, empty bar). Used when a
    /// message goes stale and the user has opted out of holding the last value.
    /// </summary>
    public void Reset()
    {
        if (_value is null)
            return;
        _value = null;
        if (!_fixedRange) { _min = 0; _max = 0; }
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(HasValue));
        OnPropertyChanged(nameof(MinText));
        OnPropertyChanged(nameof(MaxText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
