using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using IxxatCanTool.ViewModels;
using Microsoft.Win32;

namespace IxxatCanTool;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    // Drives the live "grid replay" chart sweep (~15 fps); only runs while a
    // ReplayToGrid playback is active and the graph panel is visible.
    private readonly DispatcherTimer _graphTimer;
    private PlaybackMode _currentMode = PlaybackMode.ReplayToGrid;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;
        _graphTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, OnGraphTick, Dispatcher);
        _graphTimer.Stop();
        _vm.Frames.CollectionChanged += OnFramesChanged;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsRepeating))
                Dispatcher.Invoke(UpdateRepeatUi);
            else if (e.PropertyName == nameof(MainViewModel.PlaybackState))
                Dispatcher.Invoke(UpdatePlaybackUi);
            else if (e.PropertyName == nameof(MainViewModel.IsTcpServerRunning))
                Dispatcher.Invoke(() => TcpServerButton.IsChecked = _vm.IsTcpServerRunning);
        };
        Closed += (_, _) => _vm.Dispose();
    }

    private void UpdateRepeatUi()
    {
        var text = _vm.IsRepeating ? "Stop" : "Repeat";
        TxRepeatButton.Content = text;
        DbcRepeatButton.Content = text;
        TxListRepeatButton.Content = _vm.IsRepeating ? "Stop" : "Repeat All";
    }

    private void UpdatePlaybackUi()
    {
        PlayButton.Content = _vm.PlaybackState == ViewModels.PlaybackState.Playing ? "Pause" : "Play";
        UpdateLiveGraph();
    }

    /// <summary>Start/stop the live chart sweep to match playback state, and settle the chart.</summary>
    private void UpdateLiveGraph()
    {
        bool live = IsLiveReplay;
        if (live)
        {
            if (!_graphTimer.IsEnabled)
                _graphTimer.Start();
        }
        else
        {
            _graphTimer.Stop();
            // On Stop the run is over: show the whole curve. On Pause leave the
            // last swept frame on screen (frozen at the current position).
            if (_vm.PlaybackState == ViewModels.PlaybackState.Stopped
                && GraphPanel.Visibility == Visibility.Visible)
                RedrawPlot();
        }
    }

    private bool IsLiveReplay =>
        _vm.PlaybackState == ViewModels.PlaybackState.Playing
        && _currentMode == PlaybackMode.ReplayToGrid
        && GraphPanel.Visibility == Visibility.Visible;

    private void OnGraphTick(object? sender, EventArgs e)
    {
        if (IsLiveReplay)
            RedrawPlot(_vm.PlaybackTime);
    }

    private static int ParseInterval(string text) =>
        int.TryParse(text, out int v) && v > 0 ? v : 100;

    private bool _scrollQueued;

    private void OnFramesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !_vm.AutoScroll)
            return;

        // Do NOT scroll synchronously here: ScrollIntoView re-enters the
        // DataGrid's item generator while it is processing this very
        // CollectionChanged event, which corrupts the generator at high frame
        // rates ("ItemsControl is inconsistent with its items source").
        // Instead defer to Background priority and coalesce bursts into one scroll.
        if (_scrollQueued)
            return;
        _scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollQueued = false;
            if (_vm.AutoScroll && _vm.Frames.Count > 0)
                TraceGrid.ScrollIntoView(_vm.Frames[^1]);
        }));
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _vm.RefreshDevices();

    private async void OnConnect(object sender, RoutedEventArgs e) => await _vm.ConnectAsync();

    private void OnDisconnect(object sender, RoutedEventArgs e) => _vm.Disconnect();

    private void OnSend(object sender, RoutedEventArgs e) =>
        _vm.Send(TxId.Text, TxData.Text, TxExtended.IsChecked == true, TxRemote.IsChecked == true);

    private void OnRepeatRaw(object sender, RoutedEventArgs e)
    {
        if (_vm.IsRepeating)
            _vm.StopRepeat();
        else
            _vm.StartRepeatRaw(TxId.Text, TxData.Text,
                TxExtended.IsChecked == true, TxRemote.IsChecked == true,
                ParseInterval(TxInterval.Text));
    }

    private void OnSendSignal(object sender, RoutedEventArgs e) =>
        _vm.SendDbcSignal(DbcValue.Text);

    private void OnRepeatSignal(object sender, RoutedEventArgs e)
    {
        if (_vm.IsRepeating)
            _vm.StopRepeat();
        else
            _vm.StartRepeatDbcSignal(DbcValue.Text, ParseInterval(DbcInterval.Text));
    }

    // ---- Multi-signal TX list ----

    private void OnAddTxEntry(object sender, RoutedEventArgs e) => _vm.AddTxEntry(DbcValue.Text);

    private void OnRemoveTxEntry(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TxSignalEntry entry })
            _vm.RemoveTxEntry(entry);
    }

    private void OnClearTxList(object sender, RoutedEventArgs e) => _vm.ClearTxList();

    private void OnSendTxList(object sender, RoutedEventArgs e) => _vm.SendTxList();

    private void OnRepeatTxList(object sender, RoutedEventArgs e)
    {
        if (_vm.IsRepeating)
            _vm.StopRepeat();
        else
            _vm.StartRepeatTxList(ParseInterval(DbcInterval.Text));
    }

    private void OnClear(object sender, RoutedEventArgs e) => _vm.ClearFrames();

    private void OnLoadDbc(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Load DBC database",
            Filter = "CAN database (*.dbc)|*.dbc|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            _vm.LoadDbc(dlg.FileName);
    }

    private void OnClearDbc(object sender, RoutedEventArgs e) => _vm.ClearDbc();

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open CAN trace log",
            Filter = "CSV trace (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            _vm.LoadLog(dlg.FileName);
            if (GraphToggle.IsChecked == true)
                RefreshGraph(); // new file → fresh (unselected) signal list
        }
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        var mode = PlayMode.SelectedIndex switch
        {
            0 => PlaybackMode.ViewAll,
            2 => PlaybackMode.ReplayOnBus,
            3 => PlaybackMode.ReplayToTcp,
            _ => PlaybackMode.ReplayToGrid
        };
        double speed = double.TryParse(PlaySpeed.Text, out double s) && s >= 0 ? s : 1.0;
        // Remember the mode for the live-graph decision; resume keeps the same mode.
        if (_vm.PlaybackState == ViewModels.PlaybackState.Stopped)
            _currentMode = mode;
        _vm.TogglePlayback(mode, speed);
    }

    private void OnStopPlay(object sender, RoutedEventArgs e) => _vm.StopPlayback();

    private void OnToggleLoop(object sender, RoutedEventArgs e) =>
        _vm.LoopPlayback = LoopButton.IsChecked == true;

    private void OnToggleTcpServer(object sender, RoutedEventArgs e) => _vm.ToggleTcpServer();

    // ---- Graph view ----

    private void OnToggleGraph(object sender, RoutedEventArgs e)
    {
        bool show = GraphToggle.IsChecked == true;
        GraphPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GraphSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GraphRow.Height = show ? new GridLength(280) : new GridLength(0);
        if (show)
            RefreshGraph();
        UpdateLiveGraph(); // start/stop the sweep if toggled during a replay
    }

    private void OnPlotSignalToggled(object sender, RoutedEventArgs e) => RefreshGraph();

    private void OnPlotAll(object sender, RoutedEventArgs e) => SetAllPlotSignals(true);

    private void OnPlotNone(object sender, RoutedEventArgs e) => SetAllPlotSignals(false);

    private void SetAllPlotSignals(bool selected)
    {
        foreach (var s in _vm.PlotSignals)
            s.IsSelected = selected;
        RefreshGraph();
    }

    /// <summary>Redraw clipped to the playback cursor while live-replaying, else the full curves.</summary>
    private void RefreshGraph() => RedrawPlot(IsLiveReplay ? _vm.PlaybackTime : null);

    /// <summary>
    /// Draw the selected signals. With <paramref name="cursorTime"/> set, only samples up to
    /// that time are drawn and the axes are pinned to the full log range so the trace sweeps
    /// across a stable chart instead of rescaling every frame.
    /// </summary>
    private void RedrawPlot(double? cursorTime = null)
    {
        var plot = GraphPlot.Plot;
        plot.Clear();

        bool any = false;
        double xMin = double.MaxValue, xMax = double.MinValue;
        double yMin = double.MaxValue, yMax = double.MinValue;

        foreach (var s in _vm.PlotSignals)
        {
            if (!s.IsSelected || s.Times.Length == 0)
                continue;
            any = true;

            // Track the full extent of every selected signal for stable live axes.
            xMin = Math.Min(xMin, s.Times[0]);
            xMax = Math.Max(xMax, s.Times[^1]);
            foreach (double v in s.Values)
            {
                if (v < yMin) yMin = v;
                if (v > yMax) yMax = v;
            }

            double[] xs = s.Times, ys = s.Values;
            if (cursorTime is double ct)
            {
                int n = CountUpTo(s.Times, ct);
                if (n == 0)
                    continue; // selected, but nothing has been replayed yet
                xs = s.Times[..n];
                ys = s.Values[..n];
            }

            var scatter = plot.Add.Scatter(xs, ys);
            scatter.LegendText = s.Label;
        }

        plot.Axes.Bottom.Label.Text = "Time (s)";
        plot.ShowLegend();

        if (any && cursorTime is not null && xMax > xMin && yMax >= yMin)
        {
            double yPad = Math.Max((yMax - yMin) * 0.08, 1e-6);
            plot.Axes.SetLimits(xMin, xMax, yMin - yPad, yMax + yPad);
        }
        else if (any)
        {
            plot.Axes.AutoScale();
        }

        GraphPlot.Refresh();
    }

    /// <summary>Count of leading samples whose time is ≤ <paramref name="t"/> (times are ascending).</summary>
    private static int CountUpTo(double[] times, double t)
    {
        int lo = 0, hi = times.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (times[mid] <= t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // ---- Live gauges ----

    private void OnGaugeAll(object sender, RoutedEventArgs e) => SetAllGauges(true);

    private void OnGaugeNone(object sender, RoutedEventArgs e) => SetAllGauges(false);

    private void SetAllGauges(bool enabled)
    {
        foreach (var g in _vm.CanIdGroups)
            g.IsEnabled = enabled;
    }

    private void OnToggleLog(object sender, RoutedEventArgs e)
    {
        _vm.ToggleLogging(() =>
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save CAN trace",
                Filter = "CSV trace (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = $"can-trace-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        });
        LogButton.Content = _vm.IsLogging ? "Stop Log" : "Start Log";
    }
}
