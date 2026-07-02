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
}
