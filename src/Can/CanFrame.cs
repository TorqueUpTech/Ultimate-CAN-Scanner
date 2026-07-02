namespace IxxatCanTool.Can;

public enum CanDirection
{
    Rx,
    Tx
}

/// <summary>
/// Driver-agnostic representation of a single CAN frame. Keeping this decoupled
/// from the VCI <c>ICanMessage</c> type means the UI, logger and decoders never
/// reference the vendor SDK directly.
/// </summary>
public sealed class CanFrame
{
    /// <summary>29-bit mask for the arbitration ID (strips any driver flag bits).</summary>
    public const uint IdentifierMask = 0x1FFFFFFF;

    /// <summary>Seconds since the channel was activated (VCI hardware timestamp).</summary>
    public double TimeStamp { get; init; }

    public CanDirection Direction { get; init; }

    public uint Identifier { get; init; }

    public bool IsExtended { get; init; }

    public bool IsRemote { get; init; }

    public byte[] Data { get; init; } = [];

    public int Dlc => Data.Length;

    public string DataHex => Convert.ToHexString(Data);
}
