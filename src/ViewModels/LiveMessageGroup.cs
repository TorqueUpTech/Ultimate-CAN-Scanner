using System.ComponentModel;
using System.Linq;
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
    private bool _isVisible = true;

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

    /// <summary>True when at least one of this ID's signals appears in the loaded log.</summary>
    public bool HasLogData => Signals.Any(s => s.HasLogData);

    /// <summary>
    /// Whether this ID is shown in the checklist. Lowered by the view model when the "only signals
    /// with data in log" option is on and none of the ID's signals appear in the log; otherwise true.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
                return;
            _isVisible = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
