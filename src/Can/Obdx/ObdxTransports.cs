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
