using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace IxxatCanTool.Can.J2534;

/// <summary>
/// A live SAE J2534-1 (v04.04) CAN session over one vendor PassThru DLL: opens the device,
/// connects a raw <c>CAN</c> channel at a bit rate, installs pass-all filters (11- and 29-bit),
/// drains RX on a background thread, and writes TX on demand. It is deliberately transport- and
/// bitness-agnostic — the same source compiles into the x64 app (in-process, for 64-bit drivers)
/// and the x86 host process (for 32-bit drivers reached over the bridge).
///
/// It raises RX frames only; the caller adds any TX echo into its own trace. Errors on the RX
/// loop surface via <see cref="Error"/> rather than throwing on a background thread.
/// </summary>
internal sealed class J2534CanChannel : IJ2534Transport
{
    private const int RxBatch = 32;          // messages requested per PassThruReadMsgs call
    private const uint ReadTimeoutMs = 100;   // bounds the RX loop so it can observe _running
    private const uint WriteTimeoutMs = 100;

    private readonly object _writeLock = new();
    private readonly Stopwatch _clock = new();

    private J2534Library? _lib;
    private uint _deviceId;
    private uint _channelId;
    private IntPtr _rxBuffer;
    private int _msgSize;

    private Thread? _rxThread;
    private volatile bool _running;

    /// <summary>Raised for each received frame (RX only), on the background read thread.</summary>
    public event Action<CanFrame>? FrameReceived;

    /// <summary>Raised on a fatal RX-loop error, on the background read thread.</summary>
    public event Action<string>? Error;

    /// <summary>Seconds since the channel opened (monotonic), for frame timestamps.</summary>
    public double Elapsed => _clock.Elapsed.TotalSeconds;

    /// <summary>
    /// Open <paramref name="dllPath"/>, connect a CAN channel at <paramref name="baud"/> and start
    /// receiving. Throws <see cref="BadImageFormatException"/> if the DLL's bitness does not match
    /// this process, or <see cref="IOException"/> on a PassThru failure.
    /// </summary>
    public void Open(string dllPath, uint baud)
    {
        _lib = new J2534Library(dllPath);
        try
        {
            Check(_lib.Open(IntPtr.Zero, out _deviceId), "PassThruOpen");
            Check(_lib.Connect(_deviceId, J2534Api.CAN, 0, baud, out _channelId), "PassThruConnect");

            // J2534 blocks all RX until a filter is installed. Pass everything, for both ID widths.
            InstallPassAllFilter(extended: false);
            InstallPassAllFilter(extended: true);

            _msgSize = Marshal.SizeOf<PassThruMsg>();
            _rxBuffer = Marshal.AllocHGlobal(_msgSize * RxBatch);
            // Zero it once so that if a driver returns timeout/empty without updating numMsgs, the
            // stale structs read as DataSize 0 and are skipped rather than surfacing garbage.
            Marshal.Copy(new byte[_msgSize * RxBatch], 0, _rxBuffer, _msgSize * RxBatch);

            _clock.Restart();
            _running = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "J2534-Rx" };
            _rxThread.Start();
        }
        catch
        {
            Cleanup();
            throw;
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

    /// <summary>Transmit one classic-CAN frame. Throws on a PassThru write failure.</summary>
    public void Send(uint identifier, bool extended, byte[] data)
    {
        if (_lib is null)
            throw new InvalidOperationException("Channel not open.");
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
                        Error?.Invoke($"J2534 read error 0x{rc:X2}: {_lib.LastError()}");
                    return;
                }

                for (uint i = 0; i < numMsgs; i++)
                    DispatchRx(_rxBuffer + (int)i * _msgSize);
            }
        }
        catch (Exception ex) when (_running)
        {
            Error?.Invoke("J2534 RX stopped: " + ex.Message);
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

    private void Check(uint rc, string op)
    {
        if (rc != J2534Api.STATUS_NOERROR)
            throw new IOException($"{op} failed (0x{rc:X2}): {_lib?.LastError()}");
    }

    public void Dispose()
    {
        _running = false;
        if (_rxThread is { } t && t.IsAlive && t != Thread.CurrentThread)
            t.Join(TimeSpan.FromSeconds(2));
        _rxThread = null;
        Cleanup();
    }

    private void Cleanup()
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
}
