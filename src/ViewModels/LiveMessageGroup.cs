using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IxxatCanTool.ViewModels;

/// <summary>
/// A CAN ID (one DBC message) that can be ticked on to watch its signals as live
/// gauges. Holds the message's <see cref="LiveSignal"/>s so their values persist
/// across enable/disable toggles.
/// </summary>
public sealed class LiveMessageGroup : INotifyPropertyChanged
{
    private bool _isEnabled;

    public LiveMessageGroup(uint id, string idText, string name, IReadOnlyList<LiveSignal> signals)
    {
        Id = id;
        IdText = idText;
        Name = name;
        Signals = signals;
    }

    /// <summary>Masked 11/29-bit identifier, used to match incoming frames.</summary>
    public uint Id { get; }
    public string IdText { get; }
    public string Name { get; }
    public IReadOnlyList<LiveSignal> Signals { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
                return;
            _isEnabled = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
