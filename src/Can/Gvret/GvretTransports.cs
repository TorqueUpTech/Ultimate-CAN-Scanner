using System.IO;
using System.IO.Ports;
using System.Net.Sockets;

namespace IxxatCanTool.Can.Gvret;

/// <summary>
/// A byte pipe to a GVRET/ESP32RET device — USB virtual COM or WiFi TCP. The adapter speaks the
/// GVRET binary protocol over this; the link only moves bytes. <see cref="Read"/> blocks up to an
/// implementation timeout and returns 0 when nothing arrived (so the RX loop can poll a running
/// flag), or throws once the link is genuinely closed.
/// </summary>
public interface IGvretLink : IDisposable
{
    void Open();
    void Write(byte[] data);
    int Read(byte[] buffer);
    void Close();
}

/// <summary>GVRET over a USB virtual COM port (ESP32 CDC ignores the line rate).</summary>
public sealed class SerialGvretLink : IGvretLink
{
    private readonly string _portName;
    private readonly int _baud;
    private SerialPort? _port;

    public SerialGvretLink(string portName, int baud = 1_000_000)
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

/// <summary>GVRET over a WiFi TCP socket (ESP32RET SoftAP, default 192.168.4.1:23).</summary>
public sealed class TcpGvretLink : IGvretLink
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpGvretLink(string host, int port)
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
