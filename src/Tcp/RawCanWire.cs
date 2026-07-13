using IxxatCanTool.Can;

namespace IxxatCanTool.Tcp;

/// <summary>
/// The fixed 13-byte raw-CAN wire format shared with the CAN-Replay tool, the
/// GM-ECU-Simulator's RawCanTcpServer and the Can-Display dash's SimCanBridge:
///
/// <list type="bullet">
/// <item>byte 0 — bits[3:0] = DLC (0..8), bit[7] = extended-ID flag</item>
/// <item>bytes 1-4 — CAN ID, 32-bit big-endian</item>
/// <item>bytes 5-12 — data bytes (only DLC meaningful; rest zero-padded)</item>
/// </list>
///
/// No length prefix — both ends always read exactly 13 bytes per frame.
/// </summary>
public static class RawCanWire
{
    public const int FrameSize = 13;

    /// <summary>Pack a CAN frame into a freshly allocated 13-byte wire buffer.</summary>
    public static byte[] Encode(CanFrame f)
    {
        int dlc = f.Data.Length > 8 ? 8 : f.Data.Length;
        var w = new byte[FrameSize];
        w[0] = (byte)(dlc | (f.IsExtended ? 0x80 : 0));
        w[1] = (byte)(f.Identifier >> 24);
        w[2] = (byte)(f.Identifier >> 16);
        w[3] = (byte)(f.Identifier >> 8);
        w[4] = (byte)f.Identifier;
        Array.Copy(f.Data, 0, w, 5, dlc);
        return w;
    }

    /// <summary>
    /// Unpack one 13-byte wire buffer into an inbound (<see cref="CanDirection.Rx"/>) frame.
    /// <paramref name="wire"/> must be exactly <see cref="FrameSize"/> bytes.
    /// </summary>
    public static CanFrame Decode(ReadOnlySpan<byte> wire)
    {
        if (wire.Length != FrameSize)
            throw new ArgumentException($"RawCanWire frame must be {FrameSize} bytes.", nameof(wire));

        int dlc = wire[0] & 0x0F;
        if (dlc > 8) dlc = 8;
        bool extended = (wire[0] & 0x80) != 0;
        uint id = ((uint)wire[1] << 24) | ((uint)wire[2] << 16) | ((uint)wire[3] << 8) | wire[4];

        return new CanFrame
        {
            Direction = CanDirection.Rx,
            Identifier = id & CanFrame.IdentifierMask,
            IsExtended = extended,
            Data = wire.Slice(5, dlc).ToArray()
        };
    }
}
