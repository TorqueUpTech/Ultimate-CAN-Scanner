using System.Globalization;
using System.IO;
using System.Text;
using IxxatCanTool.Can;

namespace IxxatCanTool.Logging;

/// <summary>
/// Appends CAN frames to a CSV trace file. Thread-safe: frames arrive on the
/// VCI RX thread while the UI thread may start/stop logging.
/// </summary>
public sealed class TraceLogger : IDisposable
{
    private readonly object _sync = new();
    private StreamWriter? _writer;

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
            _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
            _writer.WriteLine("Time(s),Dir,ID,Type,DLC,Data");
            CurrentPath = path;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
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
