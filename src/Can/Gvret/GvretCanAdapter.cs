using System.Diagnostics;
using System.IO;
using System.IO.Ports;

namespace IxxatCanTool.Can.Gvret;

/// <summary>
/// <see cref="ICanAdapter"/> backed by a GVRET / ESP32RET device (the firmware SavvyCAN talks to)
/// over the GVRET binary serial protocol (<see cref="GvretProtocol"/>), across USB serial or WiFi
/// TCP. On connect it switches the device into binary mode, probes it with a KEEPALIVE, enables
/// CAN0 at the chosen bit rate (SETUP_CANBUS), then pumps the inbound stream through
/// <see cref="GvretFrameParser"/> and republishes CAN frames.
///
/// The device does not self-receive transmitted frames, so TX is mirrored into the trace like the
/// OBDX/J2534 paths. Cyclic TX uses a software timer (no hardware scheduler), so
/// <see cref="SupportsScheduler"/> is false. Timestamps are taken on arrival for consistency with
/// the other backends (the device's micros counter is available on each frame but wraps ~71 min).
/// </summary>
public sealed class GvretCanAdapter : ICanAdapter
{
    private readonly object _writeLock = new();
    private readonly object _cyclicLock = new();
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<int, SoftCyclic> _cyclic = [];
    private int _nextCyclicHandle;

    private IGvretLink? _link;
    private Thread? _rxThread;
    private volatile bool _running;

    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? BusError;

    public bool IsConnected { get; private set; }

    public bool SupportsScheduler => false;

    /// <summary>
    /// GVRET candidates: each USB serial port, plus the common ESP32RET WiFi SoftAP endpoint. WiFi
    /// discovery isn't broadcast, so the fixed 192.168.4.1:23 entry is offered for the SoftAP case.
    /// </summary>
    public static IReadOnlyList<CanDeviceInfo> EnumerateDevices()
    {
        var list = new List<CanDeviceInfo>();
        foreach (string port in SerialPort.GetPortNames().Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            list.Add(new CanDeviceInfo(CanAdapterKind.Gvret, $"serial:{port}", "GVRET / ESP32RET (USB)", port));

        list.Add(new CanDeviceInfo(CanAdapterKind.Gvret, "tcp:192.168.4.1:23", "GVRET / ESP32RET (WiFi)", "192.168.4.1:23"));
        return list;
    }

    public void Connect(CanDeviceInfo device, CanBitRate bitRate, bool listenOnly = false)
    {
        if (IsConnected)
            throw new InvalidOperationException("Already connected.");
        if (device.Adapter != CanAdapterKind.Gvret)
            throw new ArgumentException($"Not a GVRET device: {device.Adapter}.", nameof(device));

        int bps = GvretProtocol.BitRateBps(bitRate); // throws early on an unsupported rate
        _link = CreateLink(device.Key);
        try
        {
            _link.Open();

            // Switch to binary mode and probe with a KEEPALIVE; require the device to say something
            // back (its keepalive reply, or bus traffic) so a wrong port fails loudly.
            WriteRaw(GvretProtocol.EnterBinary());
            WriteRaw(GvretProtocol.Keepalive());
            if (!AwaitAnyResponse(750))
                throw new IOException("No response from GVRET device (wrong port, or device not in GVRET mode).");

            // Enable CAN0 at the selected rate, then start the RX pump.
            WriteRaw(GvretProtocol.SetupCanbus(bps, listenOnly));

            _clock.Restart();
            _running = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Gvret-Rx" };
            _rxThread.Start();

            IsConnected = true;
        }
        catch
        {
            _running = false;
            SafeCloseLink();
            throw;
        }
    }

    /// <summary>Parse a device key into a link: <c>serial:COM5</c> or <c>tcp:host:port</c>.</summary>
    private static IGvretLink CreateLink(string key)
    {
        if (key.StartsWith("serial:", StringComparison.OrdinalIgnoreCase))
            return new SerialGvretLink(key["serial:".Length..]);

        if (key.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = key["tcp:".Length..];
            int colon = rest.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(rest[(colon + 1)..], out int port))
                throw new ArgumentException($"Bad GVRET key '{key}'. Expected tcp:host:port.");
            return new TcpGvretLink(rest[..colon], port);
        }

        throw new ArgumentException($"Unsupported GVRET device key '{key}'.");
    }

    /// <summary>Read until any byte arrives (device is alive) or the timeout elapses.</summary>
    private bool AwaitAnyResponse(int timeoutMs)
    {
        var buf = new byte[256];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (_link!.Read(buf) > 0)
                return true;
        }
        return false;
    }

    private void WriteRaw(byte[] data)
    {
        lock (_writeLock)
            _link!.Write(data);
    }

    private void ReceiveLoop()
    {
        var parser = new GvretFrameParser();
        var buf = new byte[4096];
        try
        {
            while (_running)
            {
                int n = _link!.Read(buf);
                if (n <= 0)
                    continue; // idle read timeout — loop to re-check _running
                parser.Append(buf.AsSpan(0, n));
                while (parser.TryRead(out GvretRxFrame f))
                    FrameReceived?.Invoke(new CanFrame
                    {
                        TimeStamp = _clock.Elapsed.TotalSeconds,
                        Direction = CanDirection.Rx,
                        Identifier = f.Id,
                        IsExtended = f.Extended,
                        Data = f.Data
                    });
            }
        }
        catch (Exception ex) when (_running)
        {
            BusError?.Invoke("GVRET RX stopped: " + ex.Message);
        }
    }

    public void Send(uint identifier, bool extended, byte[] data, bool remote = false)
    {
        if (!IsConnected || _link is null)
            throw new InvalidOperationException("Not connected.");
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));
        if (remote)
            throw new NotSupportedException("GVRET binary protocol has no remote-frame (RTR) transmit.");

        WriteRaw(GvretProtocol.BuildFrame(identifier, extended, data));

        // No self-reception over GVRET, so mirror the TX into the trace like the VCI path does.
        FrameReceived?.Invoke(new CanFrame
        {
            TimeStamp = _clock.Elapsed.TotalSeconds,
            Direction = CanDirection.Tx,
            Identifier = identifier,
            IsExtended = extended,
            Data = (byte[])data.Clone()
        });
    }

    // ---- Cyclic transmit (software timer; GVRET has no hardware scheduler surfaced here) ----

    public int StartCyclic(uint identifier, bool extended, byte[] data, bool remote, double intervalMs)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected.");
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        int period = (int)Math.Max(1, Math.Round(intervalMs));
        lock (_cyclicLock)
        {
            int handle = _nextCyclicHandle++;
            var stream = new SoftCyclic((byte[])data.Clone());
            stream.Timer = new Timer(
                _ => CyclicTick(handle, identifier, extended, stream, remote), null, 0, period);
            _cyclic[handle] = stream;
            return handle;
        }
    }

    /// <summary>Replace a running stream's payload in place (see <see cref="ICanAdapter.UpdateCyclic"/>).</summary>
    public void UpdateCyclic(int handle, byte[] data)
    {
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        lock (_cyclicLock)
            if (_cyclic.TryGetValue(handle, out SoftCyclic? stream))
                stream.Payload = (byte[])data.Clone();
    }

    private void CyclicTick(int handle, uint id, bool extended, SoftCyclic stream, bool remote)
    {
        try
        {
            Send(id, extended, stream.Payload, remote);
        }
        catch (Exception ex)
        {
            StopCyclic(handle);
            BusError?.Invoke("Cyclic transmit stopped: " + ex.Message);
        }
    }

    public void StopCyclic(int handle)
    {
        lock (_cyclicLock)
            if (_cyclic.Remove(handle, out SoftCyclic? stream))
                stream.Timer?.Dispose();
    }

    public void StopAllCyclic()
    {
        lock (_cyclicLock)
        {
            foreach (SoftCyclic stream in _cyclic.Values)
                stream.Timer?.Dispose();
            _cyclic.Clear();
        }
    }

    /// <summary>A software-timer cyclic stream whose payload can be swapped live (see UpdateCyclic).</summary>
    private sealed class SoftCyclic(byte[] payload)
    {
        public Timer? Timer;
        public volatile byte[] Payload = payload;
    }

    public void Disconnect()
    {
        _running = false;
        StopAllCyclic();

        if (_rxThread is { } t && t.IsAlive && t != Thread.CurrentThread)
            t.Join(TimeSpan.FromSeconds(2));
        _rxThread = null;

        SafeCloseLink();
        IsConnected = false;
    }

    private void SafeCloseLink()
    {
        try { _link?.Dispose(); } catch { /* best effort */ }
        _link = null;
    }

    public void Dispose() => Disconnect();
}
