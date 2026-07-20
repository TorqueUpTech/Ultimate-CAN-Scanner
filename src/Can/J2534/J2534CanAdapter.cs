using System.IO;

namespace IxxatCanTool.Can.J2534;

/// <summary>
/// <see cref="ICanAdapter"/> backed by any SAE J2534-1 (v04.04) PassThru device. The vendor DLL is
/// discovered from the registry (<see cref="J2534Registry"/>) and opened at runtime, so a single
/// build supports every installed J2534 tool.
///
/// The driver's bitness (read from its PE header) decides how it opens: a 64-bit DLL runs
/// in-process (<see cref="J2534CanChannel"/>); a 32-bit DLL — which this x64 process cannot load —
/// is reached through the bundled 32-bit host over a stdio bridge (<see cref="J2534BridgeSession"/>).
/// Either way the adapter opens a raw CAN channel at the chosen bit rate with pass-all filters,
/// surfaces RX frames, mirrors TX into the trace (J2534 does not echo transmits), and drives cyclic
/// TX with a software timer over the Send path — matching the OBDX backend.
///
/// Limitation: SAE J2534-1 has no standard listen-only mode for CAN, so that option is reported as
/// unsupported rather than silently ignored.
/// </summary>
public sealed class J2534CanAdapter : ICanAdapter
{
    private readonly object _cyclicLock = new();
    private readonly Dictionary<int, SoftCyclic> _cyclic = [];
    private int _nextCyclicHandle;

    private IJ2534Transport? _transport;

    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? BusError;

    public bool IsConnected { get; private set; }

    // Cyclic TX uses a software timer over Send (so repeats echo into the trace), like OBDX.
    public bool SupportsScheduler => false;

    /// <summary>
    /// List the installed J2534 drivers. The device <see cref="CanDeviceInfo.Key"/> is the driver
    /// DLL path (what <see cref="Connect"/> opens); the detail notes whether it runs in-process
    /// (64-bit) or through the 32-bit bridge.
    /// </summary>
    public static IReadOnlyList<CanDeviceInfo> EnumerateDevices()
    {
        var list = new List<CanDeviceInfo>();
        foreach (J2534Registry.Driver d in J2534Registry.Discover())
        {
            string file = Path.GetFileName(d.FunctionLibrary);
            string detail = PeImage.ArchOf(d.FunctionLibrary) switch
            {
                PeArch.X64 => file,
                PeArch.X86 => $"{file} — 32-bit (via bridge)",
                _ => $"{file} — unrecognised binary",
            };
            list.Add(new CanDeviceInfo(CanAdapterKind.J2534, d.FunctionLibrary, d.Name, detail));
        }
        return list;
    }

    public void Connect(CanDeviceInfo device, CanBitRate bitRate, bool listenOnly = false)
    {
        if (IsConnected)
            throw new InvalidOperationException("Already connected.");
        if (device.Adapter != CanAdapterKind.J2534)
            throw new ArgumentException($"Not a J2534 device: {device.Adapter}.", nameof(device));

        uint baud = BaudOf(bitRate);

        // Route by the actual DLL bitness: x64 in-process, x86 through the 32-bit bridge host.
        IJ2534Transport transport = PeImage.ArchOf(device.Key) switch
        {
            PeArch.X64 => new J2534CanChannel(),
            PeArch.X86 => new J2534BridgeSession(),
            _ => throw new InvalidOperationException(
                $"The J2534 driver '{Path.GetFileName(device.Key)}' is not a recognised 32- or 64-bit " +
                "Windows DLL. Reinstall the vendor software."),
        };

        transport.FrameReceived += OnTransportFrame;
        transport.Error += OnTransportError;

        try
        {
            transport.Open(device.Key, baud);
        }
        catch (BadImageFormatException)
        {
            transport.Dispose();
            throw new InvalidOperationException(
                $"The J2534 driver '{Path.GetFileName(device.Key)}' could not be loaded in-process " +
                "(bitness mismatch). Reinstall the vendor's driver.");
        }
        catch
        {
            transport.Dispose();
            throw;
        }

        _transport = transport;
        IsConnected = true;

        // SAE J2534-1 v04.04 has no standard passive/listen-only CAN mode; be honest about it.
        if (listenOnly)
            BusError?.Invoke("J2534: listen-only is not supported by SAE J2534-1; connected in normal mode (the tool will ACK).");
    }

    private void OnTransportFrame(CanFrame frame) => FrameReceived?.Invoke(frame);

    private void OnTransportError(string message) => BusError?.Invoke(message);

    public void Send(uint identifier, bool extended, byte[] data, bool remote = false)
    {
        if (!IsConnected || _transport is null)
            throw new InvalidOperationException("Not connected.");
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        _transport.Send(identifier, extended, data);

        // J2534 does not echo transmits (loopback is off), so mirror TX into the trace like VCI/OBDX.
        FrameReceived?.Invoke(new CanFrame
        {
            TimeStamp = _transport.Elapsed,
            Direction = CanDirection.Tx,
            Identifier = identifier,
            IsExtended = extended,
            IsRemote = remote,
            Data = (byte[])data.Clone()
        });
    }

    // ---- Cyclic transmit (software timer over Send; matches OBDX) ----

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
            // Hold the payload in a swappable slot so UpdateCyclic can change what each tick
            // sends without stopping the timer.
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
        StopAllCyclic();
        _transport?.Dispose();
        _transport = null;
        IsConnected = false;
    }

    private static uint BaudOf(CanBitRate br) => br switch
    {
        CanBitRate.Br10kBit => 10000,
        CanBitRate.Br20kBit => 20000,
        CanBitRate.Br50kBit => 50000,
        CanBitRate.Br100kBit => 100000,
        CanBitRate.Br125kBit => 125000,
        CanBitRate.Br250kBit => 250000,
        CanBitRate.Br500kBit => 500000,
        CanBitRate.Br800kBit => 800000,
        CanBitRate.Br1000kBit => 1000000,
        _ => 500000
    };

    public void Dispose() => Disconnect();
}
