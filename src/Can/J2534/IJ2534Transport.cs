namespace IxxatCanTool.Can.J2534;

/// <summary>
/// A CAN link behind the J2534 backend: either the in-process <see cref="J2534CanChannel"/> (for a
/// 64-bit driver) or the out-of-process <see cref="J2534BridgeSession"/> (a 32-bit driver reached
/// through the bridge host). Both raise RX frames and accept TX, so <c>J2534CanAdapter</c> drives
/// them identically and adds the shared concerns (TX echo, cyclic timers) on top.
/// </summary>
internal interface IJ2534Transport : IDisposable
{
    /// <summary>Raised for each received frame (RX only), on a background thread.</summary>
    event Action<CanFrame>? FrameReceived;

    /// <summary>Raised on a fatal error after opening, on a background thread.</summary>
    event Action<string>? Error;

    /// <summary>Seconds since the link opened (monotonic), for TX-echo timestamps.</summary>
    double Elapsed { get; }

    /// <summary>Open the driver at <paramref name="dllPath"/> and start receiving; throws on failure.</summary>
    void Open(string dllPath, uint baud);

    /// <summary>Transmit one classic-CAN frame.</summary>
    void Send(uint identifier, bool extended, byte[] data);
}
