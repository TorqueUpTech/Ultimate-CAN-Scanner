using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace IxxatCanTool.Can.J2534;

/// <summary>
/// <see cref="ICanAdapter"/> backed by any SAE J2534-1 (v04.04) PassThru device. The vendor DLL is
/// discovered from the registry (<see cref="J2534Registry"/>) and loaded by path at runtime
/// (<see cref="J2534Library"/>), so a single build supports every installed J2534 tool.
///
/// On connect it opens the device, connects a raw <c>CAN</c> channel at the chosen bit rate, and
/// installs pass-all filters for both 11- and 29-bit IDs (J2534 blocks all RX until a filter is
/// set). A background thread drains received frames; TX is mirrored into the trace and cyclic TX
/// uses a software timer over the proven Send path, matching the OBDX backend.
///
/// Limitations: this process is x64, so 32-bit-only drivers cannot be loaded (a clear error is
/// raised). SAE J2534-1 has no standard listen-only mode for CAN, so that option is reported as
/// unsupported rather than silently ignored.
/// </summary>
public sealed class J2534CanAdapter : ICanAdapter
{
    private const int RxBatch = 32;         // messages requested per PassThruReadMsgs call
    private const uint ReadTimeoutMs = 100;  // bounds the RX loop so it can observe _running
    private const uint WriteTimeoutMs = 100;

    private readonly object _writeLock = new();
    private readonly object _cyclicLock = new();
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<int, SoftCyclic> _cyclic = [];
    private int _nextCyclicHandle;

    private J2534Library? _lib;
    private uint _deviceId;
    private uint _channelId;
    private IntPtr _rxBuffer;
    private int _msgSize;

    private Thread? _rxThread;
    private volatile bool _running;

    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? BusError;

    public bool IsConnected { get; private set; }

    // Cyclic TX uses a software timer over Send (so repeats echo into the trace), like OBDX.
    public bool SupportsScheduler => false;

    /// <summary>
    /// List the installed J2534 drivers. The device <see cref="CanDeviceInfo.Key"/> is the driver
    /// DLL path (what <see cref="Connect"/> loads); 32-bit drivers are shown but marked unusable.
    /// </summary>
    public static IReadOnlyList<CanDeviceInfo> EnumerateDevices()
    {
        var list = new List<CanDeviceInfo>();
        foreach (J2534Registry.Driver d in J2534Registry.Discover())
        {
            string detail = d.Loadable
                ? Path.GetFileName(d.FunctionLibrary)
                : "32-bit driver — not supported in this 64-bit build";
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
        LoadLibrary(device.Key);

        try
        {
            Check(_lib!.Open(IntPtr.Zero, out _deviceId), "PassThruOpen");
            Check(_lib.Connect(_deviceId, J2534Api.CAN, 0, baud, out _channelId), "PassThruConnect");

            // J2534 blocks all RX until a filter is installed. Pass everything, for both ID widths.
            InstallPassAllFilter(extended: false);
            InstallPassAllFilter(extended: true);

            _msgSize = Marshal.SizeOf<PassThruMsg>();
            _rxBuffer = Marshal.AllocHGlobal(_msgSize * RxBatch);
            // Zero it once so that if a driver returns timeout/empty without updating numMsgs,
            // the stale structs read as DataSize 0 and are skipped rather than surfacing garbage.
            Marshal.Copy(new byte[_msgSize * RxBatch], 0, _rxBuffer, _msgSize * RxBatch);

            _clock.Restart();
            _running = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "J2534-Rx" };
            _rxThread.Start();

            IsConnected = true;
        }
        catch
        {
            CleanupNative();
            throw;
        }

        // SAE J2534-1 v04.04 has no standard passive/listen-only CAN mode; be honest about it.
        if (listenOnly)
            BusError?.Invoke("J2534: listen-only is not supported by SAE J2534-1; connected in normal mode (the tool will ACK).");
    }

    private void LoadLibrary(string dllPath)
    {
        try
        {
            _lib = new J2534Library(dllPath);
        }
        catch (BadImageFormatException)
        {
            throw new InvalidOperationException(
                $"The J2534 driver '{Path.GetFileName(dllPath)}' is 32-bit; CAN-Tool runs as a 64-bit " +
                "process and cannot load it. Install the vendor's 64-bit J2534 driver, or use the Ixxat/OBDX backend.");
        }
        catch (DllNotFoundException)
        {
            throw new InvalidOperationException($"J2534 driver not found at '{dllPath}'. Reinstall the vendor software.");
        }
    }

    /// <summary>Install a PASS filter matching every ID of the given width (mask + pattern all-zero).</summary>
    private void InstallPassAllFilter(bool extended)
    {
        uint flags = extended ? J2534Api.CAN_29BIT_ID : 0;
        PassThruMsg mask = FilterMsg(flags);
        PassThruMsg pattern = FilterMsg(flags);
        Check(_lib!.StartMsgFilter(_channelId, J2534Api.PASS_FILTER, ref mask, ref pattern, IntPtr.Zero, out _),
            $"PassThruStartMsgFilter ({(extended ? "29-bit" : "11-bit")})");
    }

    private static PassThruMsg FilterMsg(uint txFlags)
    {
        PassThruMsg m = PassThruMsg.Empty();
        m.ProtocolID = J2534Api.CAN;
        m.TxFlags = txFlags;
        m.DataSize = 4; // 4-byte CAN ID field, all zero => "don't care" for a zero mask
        return m;
    }

    public void Send(uint identifier, bool extended, byte[] data, bool remote = false)
    {
        if (!IsConnected || _lib is null)
            throw new InvalidOperationException("Not connected.");
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        PassThruMsg msg = PassThruMsg.Empty();
        msg.ProtocolID = J2534Api.CAN;
        msg.TxFlags = extended ? J2534Api.CAN_29BIT_ID : 0;
        msg.DataSize = (uint)(4 + data.Length);
        // CAN ID occupies the first 4 bytes, big-endian; payload follows.
        msg.Data[0] = (byte)(identifier >> 24);
        msg.Data[1] = (byte)(identifier >> 16);
        msg.Data[2] = (byte)(identifier >> 8);
        msg.Data[3] = (byte)identifier;
        Array.Copy(data, 0, msg.Data, 4, data.Length);

        uint numMsgs = 1;
        lock (_writeLock)
            Check(_lib.WriteMsgs(_channelId, ref msg, ref numMsgs, WriteTimeoutMs), "PassThruWriteMsgs");

        // J2534 does not echo transmits (loopback is off), so mirror TX into the trace like VCI/OBDX.
        FrameReceived?.Invoke(new CanFrame
        {
            TimeStamp = _clock.Elapsed.TotalSeconds,
            Direction = CanDirection.Tx,
            Identifier = identifier,
            IsExtended = extended,
            IsRemote = remote,
            Data = (byte[])data.Clone()
        });
    }

    private void ReceiveLoop()
    {
        try
        {
            while (_running)
            {
                uint numMsgs = RxBatch;
                uint rc = _lib!.ReadMsgs(_channelId, _rxBuffer, ref numMsgs, ReadTimeoutMs);

                // Timeout / empty are normal when idle — but a partial batch still returns them
                // with the count filled in, so always process numMsgs before deciding.
                if (rc != J2534Api.STATUS_NOERROR && rc != J2534Api.ERR_TIMEOUT && rc != J2534Api.ERR_BUFFER_EMPTY)
                {
                    if (_running)
                        BusError?.Invoke($"J2534 read error 0x{rc:X2}: {_lib.LastError()}");
                    return;
                }

                for (uint i = 0; i < numMsgs; i++)
                    DispatchRx(_rxBuffer + (int)i * _msgSize);
            }
        }
        catch (Exception ex) when (_running)
        {
            BusError?.Invoke("J2534 RX stopped: " + ex.Message);
        }
    }

    /// <summary>Read one message straight out of the unmanaged batch buffer (no full-struct copy).</summary>
    private void DispatchRx(IntPtr msgPtr)
    {
        uint rxStatus = (uint)Marshal.ReadInt32(msgPtr, PassThruMsg.RxStatusOffset);
        uint dataSize = (uint)Marshal.ReadInt32(msgPtr, PassThruMsg.DataSizeOffset);

        // Skip echoed transmits (in case a device has loopback on) and runt frames without an ID.
        if ((rxStatus & J2534Api.TX_MSG_TYPE) != 0 || dataSize < 4)
            return;

        // The CAN ID is the first 4 bytes of Data, big-endian; ReadInt32 reads them little-endian.
        uint id = BinaryPrimitives.ReverseEndianness((uint)Marshal.ReadInt32(msgPtr, PassThruMsg.DataOffset));

        int payloadLen = (int)dataSize - 4;
        var payload = new byte[payloadLen];
        if (payloadLen > 0)
            Marshal.Copy(msgPtr + PassThruMsg.DataOffset + 4, payload, 0, payloadLen);

        FrameReceived?.Invoke(new CanFrame
        {
            TimeStamp = _clock.Elapsed.TotalSeconds,
            Direction = CanDirection.Rx,
            Identifier = id,
            IsExtended = (rxStatus & J2534Api.CAN_29BIT_ID) != 0,
            Data = payload
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
        _running = false;
        StopAllCyclic();

        if (_rxThread is { } t && t.IsAlive && t != Thread.CurrentThread)
            t.Join(TimeSpan.FromSeconds(2));
        _rxThread = null;

        CleanupNative();
        IsConnected = false;
    }

    private void CleanupNative()
    {
        if (_lib is not null)
        {
            try { if (_channelId != 0) _lib.Disconnect(_channelId); } catch { /* best effort */ }
            try { _lib.Close(_deviceId); } catch { /* best effort */ }
            _lib.Dispose();
            _lib = null;
        }
        _channelId = 0;
        _deviceId = 0;

        if (_rxBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_rxBuffer);
            _rxBuffer = IntPtr.Zero;
        }
    }

    private void Check(uint rc, string op)
    {
        if (rc != J2534Api.STATUS_NOERROR)
            throw new IOException($"{op} failed (0x{rc:X2}): {_lib?.LastError()}");
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
