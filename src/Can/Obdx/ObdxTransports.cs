using System.IO;
using System.IO.Ports;
using System.Net.Sockets;

namespace IxxatCanTool.Can.Obdx;

/// <summary>
/// A byte pipe to an OBDX Pro tool (USB virtual COM, WiFi TCP, or — later — BLE). The adapter
/// speaks the DVI protocol over this; the transport only moves bytes. <see cref="Read"/> blocks
/// up to an implementation timeout and returns 0 when nothing arrived (so the RX loop can poll a
/// <c>_running</c> flag), or throws once the link is genuinely closed.
/// </summary>
public interface IObdxTransport : IDisposable
{
    void Open();
    void Write(byte[] data);
    int Read(byte[] buffer);
    void Close();
}

/// <summary>
/// Diagnostic decorator: wraps any <see cref="IObdxTransport"/> and appends a timestamped hex
/// dump of every byte written (<c>TX</c>) and read (<c>RX</c>) to a log file. Enabled by setting
/// the <c>OBDX_TRACE</c> environment variable (to <c>1</c> for the default temp-dir file, or to a
/// full path). Off by default and non-invasive; only for diagnosing a silent link.
/// </summary>
public sealed class LoggingObdxTransport : IObdxTransport
{
    private readonly IObdxTransport _inner;
    private readonly string _path;
    private readonly object _gate = new();

    public LoggingObdxTransport(IObdxTransport inner, string path)
    {
        _inner = inner;
        _path = path;
    }

    /// <summary>The trace path if OBDX_TRACE is set, else null (tracing disabled).</summary>
    public static string? TracePath()
    {
        string? v = Environment.GetEnvironmentVariable("OBDX_TRACE");
        if (string.IsNullOrWhiteSpace(v))
            return null;
        bool isFlag = v is "1" or "true" or "on" or "TRUE" or "ON";
        return isFlag ? Path.Combine(Path.GetTempPath(), "obdx-trace.log") : v;
    }

    public void Open()
    {
        Log("OPEN", ReadOnlySpan<byte>.Empty);
        _inner.Open();
    }

    public void Write(byte[] data)
    {
        Log("TX", data);
        _inner.Write(data);
    }

    public int Read(byte[] buffer)
    {
        int n = _inner.Read(buffer);
        if (n > 0)
            Log("RX", buffer.AsSpan(0, n));
        return n;
    }

    public void Close() => _inner.Close();

    public void Dispose()
    {
        Log("CLOSE", ReadOnlySpan<byte>.Empty);
        _inner.Dispose();
    }

    private void Log(string dir, ReadOnlySpan<byte> bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 3 + 32);
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(dir).Append(' ');
        foreach (byte b in bytes)
            sb.Append(b.ToString("X2")).Append(' ');
        sb.AppendLine();
        try
        {
            lock (_gate)
                File.AppendAllText(_path, sb.ToString());
        }
        catch { /* tracing is best-effort; never break comms over a log write */ }
    }
}

/// <summary>OBDX over a USB virtual COM port.</summary>
public sealed class SerialObdxTransport : IObdxTransport
{
    private readonly string _portName;
    private readonly int _baud;
    private SerialPort? _port;

    // USB CDC ignores the line rate, but SerialPort requires one; 115200 is a safe default.
    public SerialObdxTransport(string portName, int baud = 115200)
    {
        _portName = portName;
        _baud = baud;
    }

    public void Open()
    {
        _port = new SerialPort(_portName, _baud)
        {
            ReadTimeout = 150,
            WriteTimeout = 500
        };
        _port.Open();
    }

    public void Write(byte[] data) => _port!.Write(data, 0, data.Length);

    public int Read(byte[] buffer)
    {
        try { return _port!.Read(buffer, 0, buffer.Length); }
        catch (TimeoutException) { return 0; }
    }

    public void Close()
    {
        try { _port?.Close(); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        Close();
        _port?.Dispose();
        _port = null;
    }
}

/// <summary>OBDX over a WiFi TCP socket.</summary>
public sealed class TcpObdxTransport : IObdxTransport
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpObdxTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public void Open()
    {
        _client = new TcpClient();
        _client.Connect(_host, _port);
        _client.NoDelay = true;
        _stream = _client.GetStream();
        _stream.ReadTimeout = 150;
    }

    public void Write(byte[] data) => _stream!.Write(data, 0, data.Length);

    public int Read(byte[] buffer)
    {
        try { return _stream!.Read(buffer, 0, buffer.Length); }
        catch (IOException) { return 0; } // read timeout surfaces as IOException(SocketException)
    }

    public void Close()
    {
        try { _stream?.Close(); } catch { /* best effort */ }
        try { _client?.Close(); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        Close();
        _client?.Dispose();
        _client = null;
        _stream = null;
    }
}
