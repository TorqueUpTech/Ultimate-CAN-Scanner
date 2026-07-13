using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using System.IO;
using IxxatCanTool.Can;
using IxxatCanTool.Decoding;
using IxxatCanTool.Logging;
using IxxatCanTool.Tcp;

namespace IxxatCanTool.ViewModels;

/// <summary>How a loaded log is replayed.</summary>
public enum PlaybackMode
{
    /// <summary>Dump every decoded frame into the grid at once (static viewer).</summary>
    ViewAll,
    /// <summary>Stream frames into the grid honouring the recorded timing (offline).</summary>
    ReplayToGrid,
    /// <summary>Re-transmit frames onto the CAN bus at the recorded timing (needs a connection).</summary>
    ReplayOnBus,
    /// <summary>Broadcast frames over TCP in the 13-byte RawCanWire format (feeds the dash sim).</summary>
    ReplayToTcp
}

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused
}

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaxRows = 5000;
    private const int MaxRowsPerFlush = 2000;
    private const uint IdMask = CanFrame.IdentifierMask;
    // A gauge is "stale" once its CAN ID hasn't been seen for this long (blanked only if opted in).
    private const long StaleGaugeMs = 1500;

    private ICanAdapter _bus;
    private CanAdapterKind _busKind = CanAdapterKind.IxxatVci;
    private readonly TraceLogger _logger = new();
    private readonly TcpFrameServer _tcp = new();
    private readonly Dispatcher _dispatcher;

    // Frames arrive on the RX thread and are buffered here, then drained onto
    // the UI collection by a timer so a fast bus can't starve UI input.
    private readonly ConcurrentQueue<CanFrameRow> _pending = new();
    private readonly DispatcherTimer _flushTimer;

    private CanDeviceInfo? _selectedDevice;
    private CanAdapterKind _selectedAdapterKind = CanAdapterKind.IxxatVci;
    private CanBitRate _selectedBitRate = CanBitRate.Br500kBit;
    private bool _listenOnly;
    private bool _isConnected;
    private bool _isBusy;
    private bool _autoScroll = true;
    private bool _isRepeating;
    private string _status = "Idle";
    private string _tcpStatus = "TCP: off";
    private int _tcpPort = TcpFrameServer.DefaultPort;
    private string _dbcFileName = "(no DBC loaded)";
    private DbcDecoder? _dbc;
    private DbcMessageInfo? _selectedDbcMessage;
    private DbcSignalInfo? _selectedDbcSignal;
    private int _cyclicHandle = -1;
    // Which source is driving the active repeat, so a live value edit only pushes to the
    // matching stream (see UpdateRepeat* below).
    private RepeatKind _repeatKind = RepeatKind.None;
    // The signal captured when a single DBC-signal repeat started, so live value edits
    // re-encode against the same frame even if the picker selection changes.
    private DbcSignalInfo? _repeatDbcSignal;
    // Active multi-signal TX streams: one per distinct CAN ID, with the entries that feed it
    // so a live value edit can re-pack and update just that frame.
    private readonly List<CyclicTxGroup> _txGroups = [];
    // Groups that contain a rolling counter: driven by our own timer (each tick increments the
    // counter and re-sends) since a fixed-payload cyclic stream can't advance a counter. The
    // reference is swapped (never mutated) so the timer thread can iterate it lock-free.
    private volatile List<RollingTxGroup> _rollingGroups = [];
    private System.Threading.Timer? _rollTimer;

    private enum RepeatKind { None, Raw, DbcSignal, TxList }

    /// <summary>One running cyclic TX stream: its packed message, the source entries, and the adapter handle.</summary>
    private sealed record CyclicTxGroup(DbcMessageInfo Message, List<TxSignalEntry> Entries, int Handle);

    /// <summary>A TX group carrying ≥1 rolling counter; sent by <see cref="RollTick"/> each period.</summary>
    private sealed record RollingTxGroup(DbcMessageInfo Message, List<TxSignalEntry> Entries, List<RollingState> Rollers);

    /// <summary>Rolling-counter position for one entry: counts Min→Max (step = DBC factor) then wraps.</summary>
    private sealed class RollingState
    {
        public required TxSignalEntry Entry;
        public double Current;
        public double Min;
        public double Max;
        public double Step;

        public void Advance()
        {
            Current += Step;
            if (Current > Max + Step * 0.5)
                Current = Min;
        }
    }

    // ---- Log playback ----
    private IReadOnlyList<CanFrame> _logFrames = [];
    private string _logFileName = "(no log loaded)";
    private PlaybackState _playbackState = PlaybackState.Stopped;
    private string _playbackProgress = "";
    private CancellationTokenSource? _playCts;
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private Task _playTask = Task.CompletedTask;
    private volatile bool _loopPlayback;
    // Progress is written from the playback worker and published by the flush tick, so a
    // fast/unpaced replay doesn't post one dispatcher marshal per frame (input starvation).
    private volatile int _playbackDone;
    private int _playbackTotal;

    // ---- Live gauges ----
    // Latest frame seen per CAN ID (written on the RX/playback path, read on the
    // UI flush tick). Decoupling decode from arrival rate keeps gauges at the flush
    // cadence regardless of bus load.
    private readonly ConcurrentDictionary<uint, CanFrame> _latestFrame = new();

    // Arrival time (Environment.TickCount64) of the latest frame per CAN ID, so the flush tick
    // can tell when a message has stopped arriving and blank its gauges (opt-in, see ClearStaleGauges).
    private readonly ConcurrentDictionary<uint, long> _lastSeenTick = new();
    private bool _clearStaleGauges;

    // ---- CAN ID filter ----
    // Compiled acceptance filter, read on the RX/playback thread and swapped (never mutated)
    // on the UI thread, so a single volatile ref is enough. Null = pass everything. It trims
    // the trace grid and gates log replay (filtered IDs aren't transmitted/broadcast); logging
    // and the live gauges still see every frame.
    private string _filterText = "";
    private bool _filterEnabled;
    private bool _filterExclude;
    private volatile CanIdFilter? _rxFilter;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _bus = CanAdapters.Create(_busKind);
        AttachBus(_bus);
        _tcp.Log += msg => _dispatcher.BeginInvoke(() => Status = msg);
        // Inbound TCP frames flow through the same RX path as an adapter (grid, gauges, log).
        _tcp.FrameReceived += OnFrameReceived;

        _flushTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Background,
            (_, _) => OnFlushTick(),
            _dispatcher);

        RefreshDevices();
    }

    public ObservableCollection<CanDeviceInfo> Devices { get; } = [];
    public ObservableCollection<CanFrameRow> Frames { get; } = [];

    /// <summary>Per-signal time-series extracted from the loaded log, for the graph view.</summary>
    public ObservableCollection<PlotSignal> PlotSignals { get; } = [];

    /// <summary>CAN IDs from the loaded DBC; tick one to watch its signals as gauges.</summary>
    public ObservableCollection<LiveMessageGroup> CanIdGroups { get; } = [];

    /// <summary>Flat list of signals from the enabled CAN IDs, shown as live gauge cards.</summary>
    public ObservableCollection<LiveSignal> Gauges { get; } = [];

    /// <summary>
    /// Signals queued for multi-signal transmit. Entries sharing a CAN ID are packed
    /// into one frame; different IDs send as separate frames (and separate cyclic streams).
    /// </summary>
    public ObservableCollection<TxSignalEntry> TxList { get; } = [];

    public IReadOnlyList<CanBitRate> BitRates { get; } = Enum.GetValues<CanBitRate>();

    /// <summary>Adapter backends for the picker (Ixxat VCI4 / OBDX).</summary>
    public IReadOnlyList<CanAdapterKind> AdapterKinds { get; } = CanAdapters.Kinds;

    /// <summary>The selected backend; changing it re-enumerates its devices (only while disconnected).</summary>
    public CanAdapterKind SelectedAdapterKind
    {
        get => _selectedAdapterKind;
        set
        {
            if (Set(ref _selectedAdapterKind, value))
                RefreshDevices();
        }
    }

    public CanDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set => Set(ref _selectedDevice, value);
    }

    public CanBitRate SelectedBitRate
    {
        get => _selectedBitRate;
        set => Set(ref _selectedBitRate, value);
    }

    public bool ListenOnly
    {
        get => _listenOnly;
        set => Set(ref _listenOnly, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (Set(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(IsDisconnected));
                OnPropertyChanged(nameof(CanConnect));
            }
        }
    }

    public bool IsDisconnected => !IsConnected;

    /// <summary>True while an adapter connect is in flight (a BLE scan/serial open can take seconds).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                OnPropertyChanged(nameof(CanConnect));
        }
    }

    /// <summary>Connect is offered only when disconnected and not already connecting.</summary>
    public bool CanConnect => IsDisconnected && !IsBusy;

    public bool IsLogging => _logger.IsLogging;

    public bool AutoScroll
    {
        get => _autoScroll;
        set => Set(ref _autoScroll, value);
    }

    /// <summary>
    /// When on, a gauge is blanked to "no data" once its message stops arriving
    /// (<see cref="StaleGaugeMs"/>) instead of holding the last received value. Off by default.
    /// </summary>
    public bool ClearStaleGauges
    {
        get => _clearStaleGauges;
        set => Set(ref _clearStaleGauges, value);
    }

    // ---- RX ID filter ----

    /// <summary>
    /// CAN IDs (hex) the trace grid should accept, e.g. "100 7E8 0x120" or a range
    /// "700-7FF". Space/comma separated; "0x" prefix optional. Malformed tokens are
    /// ignored. Only takes effect while <see cref="FilterEnabled"/> is on.
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set { if (Set(ref _filterText, value)) RebuildRxFilter(); }
    }

    /// <summary>Turn the RX ID filter on/off. Off shows every frame.</summary>
    public bool FilterEnabled
    {
        get => _filterEnabled;
        set { if (Set(ref _filterEnabled, value)) RebuildRxFilter(); }
    }

    /// <summary>
    /// False (default) = allowlist (show only the listed IDs); true = blocklist
    /// (show everything except the listed IDs).
    /// </summary>
    public bool FilterExclude
    {
        get => _filterExclude;
        set { if (Set(ref _filterExclude, value)) RebuildRxFilter(); }
    }

    public string DbcFileName
    {
        get => _dbcFileName;
        private set => Set(ref _dbcFileName, value);
    }

    public bool IsRepeating
    {
        get => _isRepeating;
        private set => Set(ref _isRepeating, value);
    }

    /// <summary>Messages available for the DBC transmit picker.</summary>
    public ObservableCollection<DbcMessageInfo> DbcMessages { get; } = [];

    public DbcMessageInfo? SelectedDbcMessage
    {
        get => _selectedDbcMessage;
        set
        {
            if (Set(ref _selectedDbcMessage, value))
            {
                OnPropertyChanged(nameof(DbcSignals));
                SelectedDbcSignal = value?.Signals.FirstOrDefault();
            }
        }
    }

    public IReadOnlyList<DbcSignalInfo> DbcSignals =>
        SelectedDbcMessage?.Signals ?? [];

    public DbcSignalInfo? SelectedDbcSignal
    {
        get => _selectedDbcSignal;
        set => Set(ref _selectedDbcSignal, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    // ---- TCP broadcast (RawCanWire server for the dash sim) ----

    public bool IsTcpServerRunning => _tcp.Running;

    /// <summary>
    /// Port the RawCanWire server binds to; editable so several app instances can each serve a
    /// different client on its own port. Takes effect the next time the server starts.
    /// </summary>
    public int TcpPort
    {
        get => _tcpPort;
        set => Set(ref _tcpPort, value);
    }

    /// <summary>Polled by the UI flush tick: bind address + attached client count, or "off".</summary>
    public string TcpStatus
    {
        get => _tcpStatus;
        private set => Set(ref _tcpStatus, value);
    }

    /// <summary>Start/stop the RawCanWire TCP broadcast server on <see cref="TcpPort"/>.</summary>
    public void ToggleTcpServer()
    {
        try
        {
            if (_tcp.Running)
                _tcp.Stop();
            else
                _tcp.Start(TcpFrameServer.DefaultBindAddress, TcpPort);
        }
        catch (Exception ex)
        {
            Status = "TCP server failed: " + ex.Message;
        }
        OnPropertyChanged(nameof(IsTcpServerRunning));
    }

    // ---- Log playback ----

    public bool IsLogLoaded => _logFrames.Count > 0;

    public string LogFileName
    {
        get => _logFileName;
        private set => Set(ref _logFileName, value);
    }

    public PlaybackState PlaybackState
    {
        get => _playbackState;
        private set => Set(ref _playbackState, value);
    }

    public string PlaybackProgress
    {
        get => _playbackProgress;
        private set => Set(ref _playbackProgress, value);
    }

    /// <summary>
    /// When set, paced replay restarts from the first frame until Stop. The worker reads
    /// this each pass, so toggling it mid-replay takes effect after the current pass.
    /// Ignored by <see cref="PlaybackMode.ViewAll"/> (an instant dump, not a paced run).
    /// </summary>
    public bool LoopPlayback
    {
        get => _loopPlayback;
        set => _loopPlayback = value;
    }

    /// <summary>
    /// Timestamp (s) of the most recently replayed frame. Polled by the graph
    /// view to sweep a live chart out to the current position; not bound, so it
    /// is written straight from the playback worker without marshalling.
    /// </summary>
    public double PlaybackTime { get; private set; }

    /// <summary>Load a DBC database; subsequent frames decode against it.</summary>
    public void LoadDbc(string path)
    {
        try
        {
            _dbc = DbcDecoder.Load(path);
            DbcFileName = Path.GetFileName(path);

            DbcMessages.Clear();
            foreach (var m in _dbc.Messages)
                DbcMessages.Add(m);
            SelectedDbcMessage = DbcMessages.FirstOrDefault();

            BuildCanIdGroups();

            Status = $"Loaded DBC '{DbcFileName}' ({_dbc.MessageCount} messages).";
        }
        catch (Exception ex)
        {
            Status = "DBC load failed: " + ex.Message;
        }
    }

    public void ClearDbc()
    {
        _dbc = null;
        DbcMessages.Clear();
        SelectedDbcMessage = null;
        BuildCanIdGroups();
        DbcFileName = "(no DBC loaded)";
        Status = "DBC cleared.";
    }

    public void RefreshDevices()
    {
        try
        {
            Devices.Clear();
            foreach (var d in CanAdapters.Enumerate(_selectedAdapterKind))
                Devices.Add(d);
            SelectedDevice = Devices.FirstOrDefault();
            Status = $"Found {Devices.Count} {_selectedAdapterKind} device(s).";
        }
        catch (Exception ex)
        {
            Status = "Enumeration failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Open the selected device off the UI thread. The blocking adapter open (VCI enumerate,
    /// serial/TCP open, or a multi-second BLE scan) runs on the thread pool so the window stays
    /// responsive; state and status are updated back on the UI thread when it completes.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (SelectedDevice is null)
        {
            Status = "Select a device first.";
            return;
        }
        if (IsBusy || IsConnected)
            return;

        var device = SelectedDevice;
        var bitRate = SelectedBitRate;
        bool listenOnly = ListenOnly;
        try
        {
            EnsureBusKind(); // swap backend on the UI thread — it rewires events
            IsBusy = true;
            Status = $"Connecting to {device.Description}…";
            await Task.Run(() => _bus.Connect(device, bitRate, listenOnly));
            IsConnected = true;
            Status = $"Connected @ {bitRate}{(listenOnly ? " (listen-only)" : "")}.";
        }
        catch (Exception ex)
        {
            Status = "Connect failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Adapter backend wiring ----

    private void AttachBus(ICanAdapter bus)
    {
        bus.FrameReceived += OnFrameReceived;
        bus.BusError += OnBusError;
    }

    private void DetachBus(ICanAdapter bus)
    {
        bus.FrameReceived -= OnFrameReceived;
        bus.BusError -= OnBusError;
    }

    private void OnBusError(string message) => _dispatcher.BeginInvoke(() => Status = message);

    /// <summary>Swap the active backend to match the selected kind (called while disconnected).</summary>
    private void EnsureBusKind()
    {
        if (_busKind == _selectedAdapterKind)
            return;
        DetachBus(_bus);
        _bus.Dispose();
        _bus = CanAdapters.Create(_busKind = _selectedAdapterKind);
        AttachBus(_bus);
    }

    public void Disconnect()
    {
        // The bus tears down cyclic messages itself; just reset our view state.
        _cyclicHandle = -1;
        IsRepeating = false;
        _bus.Disconnect();
        IsConnected = false;
        Status = "Disconnected.";
    }

    /// <summary>Send a raw frame parsed from the UI text fields (one-shot).</summary>
    public void Send(string idText, string dataText, bool extended, bool remote)
    {
        try
        {
            SendRawCore(idText, dataText, extended, remote);
            Status = "Sent " + idText.Trim();
        }
        catch (Exception ex)
        {
            Status = "Send failed: " + ex.Message;
        }
    }

    /// <summary>Throwing core used by both one-shot Send and the repeater.</summary>
    private void SendRawCore(string idText, string dataText, bool extended, bool remote)
    {
        uint id = Convert.ToUInt32(idText.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
        byte[] data = ParseHexBytes(dataText);
        _bus.Send(id, extended, data, remote);
    }

    /// <summary>Encode the selected DBC signal value into its message and send (one-shot).</summary>
    public void SendDbcSignal(string valueText)
    {
        try
        {
            SendDbcSignalCore(valueText);
            Status = $"Sent {SelectedDbcSignal!.Name} = {valueText} on {SelectedDbcMessage!.Name}.";
        }
        catch (Exception ex)
        {
            Status = "Signal send failed: " + ex.Message;
        }
    }

    private void SendDbcSignalCore(string valueText)
    {
        if (SelectedDbcMessage is null || SelectedDbcSignal is null)
            throw new InvalidOperationException("Pick a DBC message and signal first.");

        if (!double.TryParse(valueText, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
            throw new FormatException($"'{valueText}' is not a number.");

        byte[] data = SelectedDbcSignal.Encode(value);
        _bus.Send(SelectedDbcMessage.Id, SelectedDbcMessage.Extended, data, remote: false);
    }

    // ---- Repeat / auto-transmit (hardware ICanScheduler, one stream at a time) ----

    /// <summary>Repeat a raw frame via the adapter's cyclic scheduler.</summary>
    public void StartRepeatRaw(string idText, string dataText, bool extended, bool remote, int intervalMs)
    {
        try
        {
            uint id = Convert.ToUInt32(idText.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
            byte[] data = ParseHexBytes(dataText);
            StartCyclic(id, extended, data, remote, intervalMs, idText.Trim(), RepeatKind.Raw);
        }
        catch (Exception ex)
        {
            Status = "Repeat failed: " + ex.Message;
        }
    }

    /// <summary>Repeat the selected DBC signal via the adapter's cyclic scheduler.</summary>
    public void StartRepeatDbcSignal(string valueText, int intervalMs)
    {
        try
        {
            if (SelectedDbcMessage is null || SelectedDbcSignal is null)
                throw new InvalidOperationException("Pick a DBC message and signal first.");
            if (!double.TryParse(valueText, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
                throw new FormatException($"'{valueText}' is not a number.");

            byte[] data = SelectedDbcSignal.Encode(value);
            StartCyclic(SelectedDbcMessage.Id, SelectedDbcMessage.Extended, data, remote: false, intervalMs,
                SelectedDbcSignal.Name, RepeatKind.DbcSignal);
            // Pin the signal so live value edits re-encode against it even if the picker moves.
            _repeatDbcSignal = SelectedDbcSignal;
        }
        catch (Exception ex)
        {
            Status = "Repeat failed: " + ex.Message;
        }
    }

    // ---- Live value updates while repeating (no stop/start) ----

    /// <summary>Push edited raw payload bytes to the running raw stream. Ignored otherwise.</summary>
    public void UpdateRepeatRaw(string dataText)
    {
        if (_repeatKind != RepeatKind.Raw || _cyclicHandle < 0)
            return;
        try
        {
            _bus.UpdateCyclic(_cyclicHandle, ParseHexBytes(dataText));
        }
        catch
        {
            // Half-typed/invalid hex: keep sending the last good payload.
        }
    }

    /// <summary>Re-encode the edited value into the running DBC-signal stream. Ignored otherwise.</summary>
    public void UpdateRepeatDbcValue(string valueText)
    {
        if (_repeatKind != RepeatKind.DbcSignal || _cyclicHandle < 0 || _repeatDbcSignal is null)
            return;
        if (!double.TryParse(valueText, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
            return;
        try
        {
            _bus.UpdateCyclic(_cyclicHandle, _repeatDbcSignal.Encode(value));
        }
        catch
        {
            // Out-of-range/encode error: keep sending the last good payload.
        }
    }

    /// <summary>Re-pack a TX-list entry's group and update just that frame when its value changes.</summary>
    private void OnTxEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_repeatKind != RepeatKind.TxList || e.PropertyName != nameof(TxSignalEntry.Value))
            return;
        if (sender is not TxSignalEntry entry)
            return;
        var group = _txGroups.FirstOrDefault(g => g.Entries.Contains(entry));
        if (group is null)
            return;
        try
        {
            byte[] data = group.Message.EncodeSignals(group.Entries.Select(en => (en.Signal, en.Value)));
            _bus.UpdateCyclic(group.Handle, data);
        }
        catch
        {
            // Keep sending the last good frame if this value doesn't encode.
        }
    }

    // ---- Multi-signal transmit list ----

    /// <summary>Append the currently picked DBC message/signal + value to the TX list.</summary>
    public void AddTxEntry(string valueText)
    {
        try
        {
            if (SelectedDbcMessage is null || SelectedDbcSignal is null)
                throw new InvalidOperationException("Pick a DBC message and signal first.");
            if (!double.TryParse(valueText, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
                throw new FormatException($"'{valueText}' is not a number.");

            TxList.Add(new TxSignalEntry(SelectedDbcMessage, SelectedDbcSignal, value));
            Status = $"Added {SelectedDbcSignal.Name} to TX list ({TxList.Count} entries).";
        }
        catch (Exception ex)
        {
            Status = "Add failed: " + ex.Message;
        }
    }

    public void RemoveTxEntry(TxSignalEntry entry)
    {
        if (TxList.Remove(entry))
            Status = $"Removed {entry.Signal.Name} ({TxList.Count} entries).";
    }

    public void ClearTxList()
    {
        TxList.Clear();
        Status = "TX list cleared.";
    }

    /// <summary>
    /// Group the TX list by CAN ID, pack each group's signals into one frame, and send
    /// one frame per ID (one-shot).
    /// </summary>
    public void SendTxList()
    {
        try
        {
            int frames = SendTxFramesOnce();
            Status = $"Sent {TxList.Count} signal(s) in {frames} frame(s).";
        }
        catch (Exception ex)
        {
            Status = "TX list send failed: " + ex.Message;
        }
    }

    /// <summary>Cyclically transmit the whole TX list — one stream per distinct CAN ID.</summary>
    public void StartRepeatTxList(int intervalMs)
    {
        try
        {
            if (TxList.Count == 0)
                throw new InvalidOperationException("TX list is empty.");

            StopRepeat();
            int ms = Math.Max(1, intervalMs);
            var rolling = new List<RollingTxGroup>();
            int rollerCount = 0;

            // Group by CAN ID. A group with a rolling counter can't ride a fixed-payload cyclic
            // stream, so it's driven by RollTick instead; the rest use the adapter's scheduler.
            foreach (var group in TxList.GroupBy(e => (e.Message.Id, e.Message.Extended)))
            {
                var entries = group.ToList();
                var msg = entries[0].Message;
                var rollers = entries
                    .Where(e => e.IsRolling)
                    .Select(MakeRollingState)
                    .ToList();

                if (rollers.Count == 0)
                {
                    byte[] data = msg.EncodeSignals(entries.Select(e => (e.Signal, e.Value)));
                    int handle = _bus.StartCyclic(msg.Id, msg.Extended, data, remote: false, ms);
                    _txGroups.Add(new CyclicTxGroup(msg, entries, handle));
                }
                else
                {
                    rolling.Add(new RollingTxGroup(msg, entries, rollers));
                    rollerCount += rollers.Count;
                }
            }

            _rollingGroups = rolling;
            foreach (var entry in TxList)
                entry.PropertyChanged += OnTxEntryChanged;
            _repeatKind = RepeatKind.TxList;
            IsRepeating = true;

            if (rolling.Count > 0)
                _rollTimer = new System.Threading.Timer(_ => RollTick(), null, 0, ms);

            int total = _txGroups.Count + rolling.Count;
            string mode = _bus.SupportsScheduler ? "hardware" : "software timer";
            string roll = rollerCount > 0 ? $", {rollerCount} rolling counter(s)" : "";
            Status = $"Cyclic TX list: {total} frame(s) every {ms} ms ({mode}{roll}).";
        }
        catch (Exception ex)
        {
            Status = "TX list repeat failed: " + ex.Message;
        }
    }

    /// <summary>Seed a rolling counter from its DBC range: count Min→Max, step = factor, wrap.</summary>
    private static RollingState MakeRollingState(TxSignalEntry entry)
    {
        var sig = entry.Signal;
        double step = sig.Factor > 0 ? sig.Factor : 1;
        double min = sig.Minimum;
        double max = sig.Maximum;
        if (max <= min) // no usable DBC range — fall back to the signal's bit width
            max = min + ((1L << Math.Clamp(sig.Length, 1, 31)) - 1) * step;
        return new RollingState { Entry = entry, Min = min, Max = max, Step = step, Current = min };
    }

    /// <summary>
    /// Timer tick for rolling groups: pack each group's frame with the counters at their current
    /// value (other signals read live from the list), send it, then advance every counter.
    /// </summary>
    private void RollTick()
    {
        var groups = _rollingGroups; // snapshot; StopRepeat swaps in a new list, never mutates
        try
        {
            foreach (var g in groups)
            {
                var values = g.Entries.Select(e =>
                {
                    var roller = g.Rollers.FirstOrDefault(r => ReferenceEquals(r.Entry, e));
                    return (e.Signal, roller?.Current ?? e.Value);
                });
                byte[] data = g.Message.EncodeSignals(values);
                _bus.Send(g.Message.Id, g.Message.Extended, data, remote: false);

                foreach (var r in g.Rollers)
                    r.Advance();
            }
        }
        catch (Exception ex)
        {
            // Bus dropped/disconnected mid-roll: stop and report once (marshalled to the UI).
            SetOnUi(() =>
            {
                StopRepeat();
                Status = "Rolling TX stopped: " + ex.Message;
            });
        }
    }

    private int SendTxFramesOnce()
    {
        if (TxList.Count == 0)
            throw new InvalidOperationException("TX list is empty.");
        int frames = 0;
        foreach (var (msg, data) in BuildTxFrames())
        {
            _bus.Send(msg.Id, msg.Extended, data, remote: false);
            frames++;
        }
        return frames;
    }

    /// <summary>Pack the TX list into one frame per distinct CAN ID (Id + addressing).</summary>
    private List<(DbcMessageInfo msg, byte[] data)> BuildTxFrames()
    {
        var frames = new List<(DbcMessageInfo, byte[])>();
        foreach (var group in TxList.GroupBy(e => (e.Message.Id, e.Message.Extended)))
        {
            var msg = group.First().Message;
            byte[] data = msg.EncodeSignals(group.Select(e => (e.Signal, e.Value)));
            frames.Add((msg, data));
        }
        return frames;
    }

    private void StartCyclic(uint id, bool extended, byte[] data, bool remote, int intervalMs, string label,
        RepeatKind kind)
    {
        StopRepeat();
        _cyclicHandle = _bus.StartCyclic(id, extended, data, remote, Math.Max(1, intervalMs));
        _repeatKind = kind;
        IsRepeating = true;
        string mode = _bus.SupportsScheduler ? "hardware" : "software timer";
        Status = $"Cyclic {label} every {intervalMs} ms ({mode}).";
    }

    public void StopRepeat()
    {
        if (_cyclicHandle >= 0)
        {
            _bus.StopCyclic(_cyclicHandle);
            _cyclicHandle = -1;
        }
        foreach (var group in _txGroups)
            _bus.StopCyclic(group.Handle);
        _txGroups.Clear();
        // Stop the rolling-counter driver: swap in an empty list (so an in-flight tick finishes
        // on its old snapshot) before disposing the timer.
        _rollingGroups = [];
        _rollTimer?.Dispose();
        _rollTimer = null;
        foreach (var entry in TxList)
            entry.PropertyChanged -= OnTxEntryChanged;
        _repeatKind = RepeatKind.None;
        _repeatDbcSignal = null;
        if (IsRepeating)
        {
            IsRepeating = false;
            Status = "Repeat stopped.";
        }
    }

    public void ToggleLogging(Func<string?> pickPath)
    {
        if (_logger.IsLogging)
        {
            _logger.Stop();
            Status = "Logging stopped.";
        }
        else
        {
            var path = pickPath();
            if (path is null)
                return;
            _logger.Start(path);
            Status = "Logging to " + path;
        }
        OnPropertyChanged(nameof(IsLogging));
    }

    public void ClearFrames()
    {
        _pending.Clear();
        Frames.Clear();
    }

    // ---- Log playback ---------------------------------------------------------

    /// <summary>Read a captured CSV trace into memory, ready for playback.</summary>
    public void LoadLog(string path)
    {
        StopPlayback();
        try
        {
            _logFrames = LogFile.Read(path);
            LogFileName = Path.GetFileName(path);
            OnPropertyChanged(nameof(IsLogLoaded));
            PlaybackProgress = $"0 / {_logFrames.Count:N0}";
            BuildPlotSignals();
            Status = _logFrames.Count > 0
                ? $"Loaded log '{LogFileName}' ({_logFrames.Count:N0} frames, {PlotSignals.Count:N0} plottable signals)."
                : $"Log '{LogFileName}' contained no parseable frames.";
        }
        catch (Exception ex)
        {
            _logFrames = [];
            OnPropertyChanged(nameof(IsLogLoaded));
            Status = "Log load failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Play/pause/resume entry point used by the single Play button.
    /// <see cref="PlaybackMode.ViewAll"/> always restarts as an instant dump.
    /// </summary>
    public void TogglePlayback(PlaybackMode mode, double speed)
    {
        switch (PlaybackState)
        {
            case PlaybackState.Playing:
                PausePlayback();
                break;
            case PlaybackState.Paused:
                ResumePlayback();
                break;
            default:
                StartPlayback(mode, speed);
                break;
        }
    }

    private void StartPlayback(PlaybackMode mode, double speed)
    {
        if (_logFrames.Count == 0)
        {
            Status = "Load a log first.";
            return;
        }
        if (mode == PlaybackMode.ReplayOnBus && !IsConnected)
        {
            Status = "Connect to a device before replaying onto the bus.";
            return;
        }
        if (mode == PlaybackMode.ReplayToTcp && !_tcp.Running)
        {
            // Convenience: bring the server up on demand. It stays up across plays so a
            // connected dash sim isn't dropped between passes (Stop only the playback).
            try
            {
                _tcp.Start(TcpFrameServer.DefaultBindAddress, TcpPort);
                OnPropertyChanged(nameof(IsTcpServerRunning));
            }
            catch (Exception ex)
            {
                Status = "TCP server failed: " + ex.Message;
                return;
            }
        }

        if (mode == PlaybackMode.ViewAll)
        {
            ClearFrames();
            foreach (var frame in _logFrames)
                EnqueueForDisplay(frame);
            PlaybackProgress = $"{_logFrames.Count:N0} / {_logFrames.Count:N0}";
            Status = $"Loaded {_logFrames.Count:N0} frames into the grid"
                     + (_logFrames.Count > MaxRows ? $" (grid keeps the most recent {MaxRows:N0})." : ".");
            return;
        }

        var frames = _logFrames;
        PlaybackTime = frames[0].TimeStamp;
        _playbackDone = 0;
        _playbackTotal = frames.Count;
        var cts = new CancellationTokenSource();
        _playCts = cts;
        _pauseGate.Set();
        PlaybackState = PlaybackState.Playing;
        Status = mode switch
        {
            PlaybackMode.ReplayOnBus =>
                $"Replaying {frames.Count:N0} frames onto the bus at {speed:0.##}×.",
            PlaybackMode.ReplayToTcp =>
                $"Broadcasting {frames.Count:N0} frames over TCP ({_tcp.BindAddress}:{_tcp.Port}) at {speed:0.##}×.",
            _ =>
                $"Replaying {frames.Count:N0} frames into the grid at {speed:0.##}×."
        };

        _playTask = Task.Run(() => RunPlaybackAsync(frames, mode, speed, cts.Token), cts.Token);
    }

    private async Task RunPlaybackAsync(
        IReadOnlyList<CanFrame> frames, PlaybackMode mode, double speed, CancellationToken ct)
    {
        // speed <= 0 means "as fast as possible" (no inter-frame delay).
        bool paced = speed > 0;
        try
        {
            // Each pass restarts the timing reference; loops run back-to-back (no inter-pass
            // gap). _loopPlayback is re-read here so toggling Loop mid-run ends after this pass.
            do
            {
                double prevTs = frames[0].TimeStamp;
                for (int i = 0; i < frames.Count; i++)
                {
                    _pauseGate.Wait(ct);
                    ct.ThrowIfCancellationRequested();

                    var frame = frames[i];
                    if (paced)
                    {
                        double dt = (frame.TimeStamp - prevTs) / speed;
                        if (dt > 0.0005)
                            await Task.Delay(TimeSpan.FromSeconds(dt), ct);
                    }
                    prevTs = frame.TimeStamp;
                    PlaybackTime = frame.TimeStamp;

                    // The ID filter also gates replay: a filtered-out frame is not transmitted,
                    // broadcast, or shown. Gauges still see every frame (like live RX).
                    bool emit = PassesRxFilter(frame);

                    if (mode == PlaybackMode.ReplayOnBus)
                    {
                        // The bus echoes self-received frames, so the grid updates via the RX path.
                        if (emit)
                            _bus.Send(frame.Identifier, frame.IsExtended, frame.Data, frame.IsRemote);
                        else
                            // No echo will come back for a skipped frame; keep its gauge live.
                            RecordLatest(frame);
                    }
                    else if (mode == PlaybackMode.ReplayToTcp)
                    {
                        // No self-echo over TCP, so mirror the frame into the grid ourselves.
                        if (emit)
                            _tcp.Broadcast(RawCanWire.Encode(frame));
                        EnqueueForDisplay(frame); // updates gauges always; grid honours the filter
                    }
                    else
                        EnqueueForDisplay(frame);

                    _playbackDone = i + 1; // published by the flush tick, not marshalled per frame
                }
            }
            while (_loopPlayback && !ct.IsCancellationRequested);

            SetOnUi(() =>
            {
                Status = "Playback finished.";
                FinishPlayback();
            });
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user; StopPlayback() owns the state reset.
        }
        catch (Exception ex)
        {
            SetOnUi(() =>
            {
                Status = "Playback stopped: " + ex.Message;
                FinishPlayback();
            });
        }
    }

    private void PausePlayback()
    {
        if (PlaybackState != PlaybackState.Playing)
            return;
        _pauseGate.Reset();
        PlaybackState = PlaybackState.Paused;
        Status = "Playback paused.";
    }

    private void ResumePlayback()
    {
        if (PlaybackState != PlaybackState.Paused)
            return;
        _pauseGate.Set();
        PlaybackState = PlaybackState.Playing;
        Status = "Playback resumed.";
    }

    public void StopPlayback()
    {
        if (_playCts is null)
            return;
        _playCts.Cancel();
        _pauseGate.Set(); // release the loop if it is parked in a pause
        try { _playTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        FinishPlayback();
        if (_logFrames.Count > 0)
            PlaybackProgress = $"0 / {_logFrames.Count:N0}";
    }

    private void FinishPlayback()
    {
        _playCts?.Dispose();
        _playCts = null;
        _pauseGate.Set();
        PlaybackState = PlaybackState.Stopped;
    }

    /// <summary>
    /// Decode every loaded frame against the DBC and group the numeric values into
    /// per-signal time-series for the graph. Needs a DBC; J1939 yields no numbers.
    /// </summary>
    private void BuildPlotSignals()
    {
        PlotSignals.Clear();
        if (_dbc is null || _logFrames.Count == 0)
            return;

        // key -> (message, signal, unit, samples). Preserve first-seen order.
        var series = new Dictionary<string, (string Msg, string Sig, string Unit, List<double> T, List<double> V)>();
        var order = new List<string>();

        foreach (var frame in _logFrames)
        {
            foreach (var s in _dbc.DecodeSignals(frame))
            {
                string key = s.Message + " " + s.Signal;
                if (!series.TryGetValue(key, out var entry))
                {
                    entry = (s.Message, s.Signal, s.Unit, new List<double>(), new List<double>());
                    series[key] = entry;
                    order.Add(key);
                }
                entry.T.Add(frame.TimeStamp);
                entry.V.Add(s.Value);
            }
        }

        foreach (var key in order)
        {
            var e = series[key];
            PlotSignals.Add(new PlotSignal(e.Msg, e.Sig, e.Unit, e.T.ToArray(), e.V.ToArray()));
        }
    }

    /// <summary>Record a frame as the latest for its ID and stamp its arrival time (gauge staleness).</summary>
    private void RecordLatest(CanFrame frame)
    {
        uint id = frame.Identifier & IdMask;
        _latestFrame[id] = frame;
        _lastSeenTick[id] = Environment.TickCount64;
    }

    /// <summary>Decode a frame and buffer it for the grid without writing it to the trace log.</summary>
    private void EnqueueForDisplay(CanFrame frame)
    {
        RecordLatest(frame);
        if (!PassesRxFilter(frame))
            return;
        // Pass the decoder, don't run it: the row decodes lazily only if it's rendered.
        _pending.Enqueue(new CanFrameRow(frame, DecodeFrame));
    }

    /// <summary>Marshal an action onto the UI thread (playback runs on a worker).</summary>
    private void SetOnUi(Action action) => _dispatcher.BeginInvoke(action);

    private void OnFrameReceived(CanFrame frame)
    {
        // Log and gauge-sample every frame; the filter only trims what the grid shows.
        _logger.Log(frame);
        RecordLatest(frame);
        if (!PassesRxFilter(frame))
            return;
        // Keep the RX thread cheap: log + buffer only. Decoding is deferred to the row,
        // which decodes lazily on the UI thread only if the grid actually renders it.
        _pending.Enqueue(new CanFrameRow(frame, DecodeFrame));
    }

    // ---- RX ID filter ----

    /// <summary>Trace-grid acceptance test; true when no filter is active.</summary>
    private bool PassesRxFilter(CanFrame frame)
    {
        var filter = _rxFilter;
        return filter is null || filter.Passes(frame.Identifier & IdMask);
    }

    /// <summary>(Re)compile the filter from the current text/mode, or clear it when disabled/empty.</summary>
    private void RebuildRxFilter()
    {
        if (!_filterEnabled)
        {
            _rxFilter = null;
            return;
        }

        var filter = CanIdFilter.Parse(_filterText, _filterExclude);
        _rxFilter = filter;
        if (filter is null)
        {
            Status = "ID filter on, but no valid IDs entered — showing all frames.";
            return;
        }

        Status = filter.Exclude
            ? $"ID filter: hiding {filter.Count} ID(s)/range(s)."
            : $"ID filter: showing only {filter.Count} ID(s)/range(s).";
    }

    /// <summary>Drain buffered frames onto the UI collection (runs on the UI thread).</summary>
    private void FlushPending()
    {
        int added = 0;
        while (added < MaxRowsPerFlush && _pending.TryDequeue(out var row))
        {
            Frames.Add(row);
            added++;
        }

        if (added == 0)
            return;

        int overflow = Frames.Count - MaxRows;
        for (int i = 0; i < overflow; i++)
            Frames.RemoveAt(0);
    }

    // ---- Live gauges ----------------------------------------------------------

    /// <summary>UI timer tick: drain the grid, refresh the enabled gauges, sample TCP status.</summary>
    private void OnFlushTick()
    {
        FlushPending();
        UpdateGauges();
        TcpStatus = _tcp.Running
            ? $"TCP {_tcp.BindAddress}:{_tcp.Port} — {_tcp.ClientCount} client(s)"
            : "TCP: off";

        // Publish playback progress at the flush cadence (Set() no-ops when unchanged).
        if (PlaybackState != PlaybackState.Stopped)
            PlaybackProgress = $"{_playbackDone:N0} / {_playbackTotal:N0}";
    }

    /// <summary>Rebuild the CAN-ID checklist from the loaded DBC (or clear it).</summary>
    private void BuildCanIdGroups()
    {
        foreach (var g in CanIdGroups)
        {
            g.PropertyChanged -= OnGroupEnabledChanged;
            foreach (var sig in g.Signals)
                sig.SelectionChanged -= RebuildGauges;
        }
        CanIdGroups.Clear();
        Gauges.Clear();
        _latestFrame.Clear();
        _lastSeenTick.Clear();
        if (_dbc is null)
            return;

        foreach (var msg in _dbc.Messages)
        {
            if (msg.Signals.Count == 0)
                continue; // nothing to gauge (placeholder/empty message)

            var signals = msg.Signals
                .Select(s => new LiveSignal(msg.Name, s.Name, s.Unit, s.Minimum, s.Maximum))
                .ToList();
            // Deselecting a single signal re-flattens the gauge list for its (enabled) ID.
            foreach (var sig in signals)
                sig.SelectionChanged += RebuildGauges;

            string idText = msg.Extended ? $"0x{msg.Id:X8}x" : $"0x{msg.Id:X3}";
            var group = new LiveMessageGroup(msg.Id, idText, msg.Name, signals);
            group.PropertyChanged += OnGroupEnabledChanged;
            CanIdGroups.Add(group);
        }
    }

    private void OnGroupEnabledChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveMessageGroup.IsEnabled))
            RebuildGauges();
    }

    /// <summary>Flatten the selected signals of every enabled CAN ID into the gauge list.</summary>
    private void RebuildGauges()
    {
        Gauges.Clear();
        foreach (var group in CanIdGroups)
        {
            if (!group.IsEnabled)
                continue;
            foreach (var sig in group.Signals)
                if (sig.IsSelected)
                    Gauges.Add(sig);
        }
    }

    /// <summary>Decode the latest frame of each enabled CAN ID and push values to its gauges.</summary>
    private void UpdateGauges()
    {
        if (Gauges.Count == 0 || _dbc is null)
            return;

        long now = Environment.TickCount64;
        foreach (var group in CanIdGroups)
        {
            if (!group.IsEnabled || !_latestFrame.TryGetValue(group.Id, out var frame))
                continue;

            // Opted in: once the message stops arriving, blank the gauges instead of
            // holding the last value. Reset() is a no-op after the first stale tick.
            if (_clearStaleGauges
                && _lastSeenTick.TryGetValue(group.Id, out long seen)
                && now - seen > StaleGaugeMs)
            {
                foreach (var sig in group.Signals)
                    sig.Reset();
                continue;
            }

            var decoded = _dbc.DecodeSignals(frame);
            if (decoded.Count == 0)
                continue;

            foreach (var sig in group.Signals)
            {
                foreach (var d in decoded)
                {
                    if (d.Signal == sig.SignalName)
                    {
                        sig.Update(d.Value);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>DBC decode if a database is loaded and matches, else J1939.</summary>
    private string DecodeFrame(CanFrame frame)
    {
        var dbcText = _dbc?.Decode(frame);
        if (!string.IsNullOrEmpty(dbcText))
            return dbcText;

        return J1939Decoder.TryDecode(frame)?.Summary ?? string.Empty;
    }

    private static byte[] ParseHexBytes(string text)
    {
        var tokens = text.Split([' ', ',', '-'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Select(t => Convert.ToByte(t, 16)).ToArray();
    }

    public void Dispose()
    {
        StopPlayback();
        _flushTimer.Stop();
        StopRepeat();
        _tcp.Dispose();
        _bus.Dispose();
        _logger.Dispose();
        _pauseGate.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
