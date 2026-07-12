namespace IxxatCanTool.Can.Gvret;

/// <summary>
/// The GVRET binary serial protocol (collin80/GVRET and the ESP32RET port), as spoken by
/// SavvyCAN over USB serial or WiFi TCP. Every command is framed by the 0xF1 marker; received
/// CAN frames stream as command 0x00. This class holds the pure wire encoding/decoding so it can
/// be unit-tested without a device. See <see cref="GvretFrameParser"/> for the inbound stream.
/// </summary>
public static class GvretProtocol
{
    /// <summary>Command marker that precedes every binary command byte, both directions.</summary>
    public const byte Marker = 0xF1;

    // Command codes (subset we use / must resync past).
    public const byte CmdBuildFrame = 0x00;      // CAN frame (RX report and TX request)
    public const byte CmdTimeSync = 0x01;
    public const byte CmdSetupCanbus = 0x05;
    public const byte CmdGetCanbusParams = 0x06;
    public const byte CmdGetDevInfo = 0x07;
    public const byte CmdKeepalive = 0x09;
    public const byte CmdEchoFrame = 0x0B;       // echoed frame, same layout as CmdBuildFrame
    public const byte CmdGetNumBuses = 0x0C;

    /// <summary>Bit 31 of the wire ID marks an extended (29-bit) frame.</summary>
    public const uint ExtendedFlag = 0x8000_0000;

    // SETUP_CANBUS speed-word flag bits (SavvyCAN piSetBusSettings).
    private const uint CfgValid = 0x8000_0000;    // "these enable/listen bits are meaningful"
    private const uint CfgEnabled = 0x4000_0000;
    private const uint CfgListenOnly = 0x2000_0000;
    private const uint SpeedMask = 0x000F_FFFF;   // firmware masks & caps at 1_000_000

    /// <summary>Two 0xE7 bytes switch a GVRET device from its text/LAWICEL console into binary mode.</summary>
    public static byte[] EnterBinary() => [0xE7, 0xE7];

    /// <summary>KEEPALIVE — the device replies 0xF1 0x09 0xDE 0xAD; used as a liveness probe.</summary>
    public static byte[] Keepalive() => [Marker, CmdKeepalive];

    /// <summary>CanBitRate to bits/second.</summary>
    public static int BitRateBps(CanBitRate rate) => rate switch
    {
        CanBitRate.Br10kBit => 10_000,
        CanBitRate.Br20kBit => 20_000,
        CanBitRate.Br50kBit => 50_000,
        CanBitRate.Br100kBit => 100_000,
        CanBitRate.Br125kBit => 125_000,
        CanBitRate.Br250kBit => 250_000,
        CanBitRate.Br500kBit => 500_000,
        CanBitRate.Br800kBit => 800_000,
        CanBitRate.Br1000kBit => 1_000_000,
        _ => throw new ArgumentOutOfRangeException(nameof(rate), rate, "Unsupported bit rate.")
    };

    /// <summary>
    /// SETUP_CANBUS: enable CAN0 at <paramref name="can0Bps"/> (optionally listen-only) and leave
    /// CAN1 disabled. Layout: 0xF1 0x05, CAN0 word (LE), CAN1 word (LE), trailing 0.
    /// </summary>
    public static byte[] SetupCanbus(int can0Bps, bool listenOnly)
    {
        uint can0 = ((uint)can0Bps & SpeedMask) | CfgValid | CfgEnabled | (listenOnly ? CfgListenOnly : 0);
        uint can1 = 0; // not valid/enabled
        return
        [
            Marker, CmdSetupCanbus,
            (byte)can0, (byte)(can0 >> 8), (byte)(can0 >> 16), (byte)(can0 >> 24),
            (byte)can1, (byte)(can1 >> 8), (byte)(can1 >> 16), (byte)(can1 >> 24),
            0x00
        ];
    }

    /// <summary>
    /// BUILD_CAN_FRAME transmit request: 0xF1 0x00, ID (LE, bit31 = extended), bus, length,
    /// data bytes, trailing 0. Matches SavvyCAN's piSendFrame.
    /// </summary>
    public static byte[] BuildFrame(uint identifier, bool extended, ReadOnlySpan<byte> data, int bus = 0)
    {
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        uint wire = (identifier & CanFrame.IdentifierMask) | (extended ? ExtendedFlag : 0);
        var buf = new byte[8 + data.Length + 1];
        buf[0] = Marker;
        buf[1] = CmdBuildFrame;
        buf[2] = (byte)wire;
        buf[3] = (byte)(wire >> 8);
        buf[4] = (byte)(wire >> 16);
        buf[5] = (byte)(wire >> 24);
        buf[6] = (byte)(bus & 0x03);
        buf[7] = (byte)data.Length;
        data.CopyTo(buf.AsSpan(8));
        buf[8 + data.Length] = 0x00; // SavvyCAN sends a trailing zero after the payload
        return buf;
    }
}

/// <summary>One CAN frame decoded from the GVRET stream.</summary>
public readonly record struct GvretRxFrame(uint Id, bool Extended, byte[] Data, uint TimestampMicros, int Bus);

/// <summary>
/// Incremental parser for the inbound GVRET binary stream. Feed it bytes with <see cref="Append"/>
/// and pull complete CAN frames with <see cref="TryRead"/>. It stays byte-aligned by knowing the
/// length of every command reply the device can send (frames are variable-length via their
/// length nibble; other replies are fixed), and resynchronises by dropping a byte if it ever sees
/// a marker followed by an unrecognised command.
/// </summary>
public sealed class GvretFrameParser
{
    private const int NeedMore = -1; // not enough bytes yet to decide/complete
    private const int Unknown = -2;  // marker + unrecognised command: resync

    // Minimum bytes to reach a frame's length nibble: marker+cmd + micros(4) + id(4) + len/bus(1).
    private const int FrameHeader = 11;

    private byte[] _buf = new byte[4096];
    private int _start;
    private int _end;

    public void Append(ReadOnlySpan<byte> data)
    {
        EnsureSpace(data.Length);
        data.CopyTo(_buf.AsSpan(_end));
        _end += data.Length;
    }

    public bool TryRead(out GvretRxFrame frame)
    {
        frame = default;
        while (_start < _end)
        {
            if (_buf[_start] != GvretProtocol.Marker)
            {
                _start++; // skip noise / leftover text until a marker
                continue;
            }

            int avail = _end - _start;
            if (avail < 2)
                break; // need the command byte

            byte cmd = _buf[_start + 1];
            int len = MessageLength(cmd, avail);
            if (len == NeedMore)
                break; // wait for the rest of this message
            if (len == Unknown)
            {
                _start++; // drop the marker and rescan; frames keep us aligned so this is rare
                continue;
            }
            if (avail < len)
                break; // full message not yet buffered

            if (cmd is GvretProtocol.CmdBuildFrame or GvretProtocol.CmdEchoFrame)
            {
                frame = ParseFrame(_start);
                _start += len;
                Compact();
                return true;
            }

            _start += len; // a known non-frame reply: consume and keep scanning
        }

        Compact();
        return false;
    }

    /// <summary>Total on-wire length (from the marker) of the message at the cursor, or a sentinel.</summary>
    private int MessageLength(byte cmd, int avail)
    {
        switch (cmd)
        {
            case GvretProtocol.CmdBuildFrame:
            case GvretProtocol.CmdEchoFrame:
                if (avail < FrameHeader)
                    return NeedMore;
                int dlc = _buf[_start + 10] & 0x0F;
                return FrameHeader + dlc + 1; // + data + trailing checksum byte
            case GvretProtocol.CmdKeepalive:
                return 4; // 0xF1 0x09 0xDE 0xAD
            case GvretProtocol.CmdTimeSync:
                return 6; // 0xF1 0x01 + micros(4)
            case GvretProtocol.CmdGetNumBuses:
                return 3; // 0xF1 0x0C + count(1)
            case GvretProtocol.CmdGetCanbusParams:
                return 12; // 0xF1 0x06 + can0(en+4) + can1(en+4)
            default:
                return Unknown;
        }
    }

    private GvretRxFrame ParseFrame(int p)
    {
        uint micros = (uint)(_buf[p + 2] | (_buf[p + 3] << 8) | (_buf[p + 4] << 16) | (_buf[p + 5] << 24));
        uint idRaw = (uint)(_buf[p + 6] | (_buf[p + 7] << 8) | (_buf[p + 8] << 16) | (_buf[p + 9] << 24));
        byte lenBus = _buf[p + 10];
        int dlc = lenBus & 0x0F;
        int bus = (lenBus >> 4) & 0x0F;

        var data = new byte[dlc];
        Array.Copy(_buf, p + FrameHeader, data, 0, dlc);

        bool extended = (idRaw & GvretProtocol.ExtendedFlag) != 0;
        uint id = idRaw & CanFrame.IdentifierMask;
        return new GvretRxFrame(id, extended, data, micros, bus);
    }

    private void EnsureSpace(int extra)
    {
        if (_end + extra <= _buf.Length)
            return;
        Compact();
        if (_end + extra <= _buf.Length)
            return;
        int cap = _buf.Length * 2;
        while (cap < _end + extra)
            cap *= 2;
        Array.Resize(ref _buf, cap);
    }

    private void Compact()
    {
        if (_start == 0)
            return;
        int remaining = _end - _start;
        if (remaining > 0)
            Array.Copy(_buf, _start, _buf, 0, remaining);
        _start = 0;
        _end = remaining;
    }
}
