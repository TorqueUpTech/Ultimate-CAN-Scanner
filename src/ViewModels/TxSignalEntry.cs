using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using IxxatCanTool.Decoding;

namespace IxxatCanTool.ViewModels;

/// <summary>
/// One queued signal in the multi-signal TX list: a DBC message + signal + the
/// physical value to transmit. The value is editable inline; entries that share a
/// CAN ID are packed into a single frame at send time.
/// </summary>
public sealed class TxSignalEntry : INotifyPropertyChanged
{
    private double _value;
    private bool _isRolling;

    public TxSignalEntry(DbcMessageInfo message, DbcSignalInfo signal, double value)
    {
        Message = message;
        Signal = signal;
        _value = value;
        _isRolling = signal.IsLikelyRollingCounter;
    }

    public DbcMessageInfo Message { get; }
    public DbcSignalInfo Signal { get; }

    /// <summary>
    /// When set, this signal is sent as a rolling counter while repeating: each transmitted
    /// frame increments it (step = DBC factor) and it wraps at the DBC min/max. The inline
    /// value box is ignored for a rolling entry. Pre-set from the signal name; user-overridable.
    /// </summary>
    public bool IsRolling
    {
        get => _isRolling;
        set
        {
            if (_isRolling == value)
                return;
            _isRolling = value;
            OnPropertyChanged();
        }
    }

    public double Value
    {
        get => _value;
        set
        {
            if (_value.Equals(value))
                return;
            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueText));
        }
    }

    /// <summary>Two-way text binding for inline editing; ignores non-numeric input.</summary>
    public string ValueText
    {
        get => _value.ToString("0.###", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                Value = v;
        }
    }

    public string IdText => Message.Extended ? $"0x{Message.Id:X8}x" : $"0x{Message.Id:X3}";

    public string SignalText =>
        string.IsNullOrEmpty(Signal.Unit) ? Signal.Name : $"{Signal.Name} [{Signal.Unit}]";

    public string MessageText => $"{Message.Name} ({IdText})";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
