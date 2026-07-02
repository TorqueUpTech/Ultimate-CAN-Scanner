using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using IxxatCanTool.Can;

namespace IxxatCanTool.Logging;

/// <summary>
/// Appends CAN frames to a CSV trace file. Thread-safe: frames arrive on the
/// RX thread while the UI thread may start/stop logging.
///
/// Writes are buffered (not flushed per line) and pushed to disk by a background
/// timer every <see cref="FlushIntervalMs"/> ms and on <see cref="Stop"/>. A busy
/// bus therefore costs one flush twice a second rather than a disk hit per frame
/// (the old <c>AutoFlush = true</c>), at the cost of losing at most ½s of trace on
/// a hard crash.
/// </summary>
public sealed class TraceLogger : IDisposable
{
    private const int FlushIntervalMs = 500;

    private readonly object _sync = new();
    private StreamWriter? _writer;
    private Timer? _flushTimer;

    public bool IsLogging
    {
        get { lock (_sync) return _writer is not null; }
    }

    public string? CurrentPath { get; private set; }

    public void Start(string path)
    {
        lock (_sync)
        {
            Stop();
            // 64 KB write buffer + no AutoFlush; the timer owns durability.
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
            _writer = new StreamWriter(stream, Encoding.UTF8);
            _writer.WriteLine("Time(s),Dir,ID,Type,DLC,Data");
            CurrentPath = path;
            _flushTimer = new Timer(_ => FlushSafe(), null, FlushIntervalMs, FlushIntervalMs);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void FlushSafe()
    {
        lock (_sync)
        {
            try { _writer?.Flush(); } catch { /* disk full / disposed — Stop handles cleanup */ }
        }
    }

    public void Log(CanFrame frame)
    {
        lock (_sync)
        {
            if (_writer is null)
                return;

            var idText = frame.IsExtended
                ? $"{frame.Identifier:X8}x"
                : $"{frame.Identifier:X3}";

            _writer.WriteLine(string.Join(',',
                frame.TimeStamp.ToString("F6", CultureInfo.InvariantCulture),
                frame.Direction,
                idText,
                frame.IsRemote ? "RTR" : "DATA",
                frame.Dlc,
                frame.DataHex));
        }
    }

    public void Dispose() => Stop();
}
