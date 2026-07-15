namespace IxxatCanTool.Can;

/// <summary>
/// A CAN adapter backend. Confines all vendor/transport specifics behind one surface so the
/// UI and view model stay adapter-agnostic. Implemented by the Ixxat VCI4 driver
/// (<see cref="CanBusService"/>) and the OBDX Pro tool (<c>ObdxCanAdapter</c>).
/// </summary>
public interface ICanAdapter : IDisposable
{
    /// <summary>Raised for every received (or self-received) frame, on a background thread.</summary>
    event Action<CanFrame>? FrameReceived;

    /// <summary>Raised on a bus error / state change, on a background thread.</summary>
    event Action<string>? BusError;

    bool IsConnected { get; }

    /// <summary>True when cyclic frames transmit without a software timer (hardware scheduler).</summary>
    bool SupportsScheduler { get; }

    /// <summary>
    /// True when this backend can push an ID filter into the device itself, so non-matching frames
    /// are dropped at the tool and never cross the link. Unlike the trace-view <see cref="CanIdFilter"/>,
    /// that saves bandwidth (the point on WiFi/BLE) but the frames are genuinely gone — they reach
    /// neither the grid nor the CSV log.
    /// </summary>
    bool SupportsDeviceFilter => false;

    /// <summary>
    /// Push <paramref name="filter"/> into the device; null clears it (pass everything). Safe to call
    /// before <see cref="Connect"/> (applied during init, so no unfiltered burst) or while connected
    /// (pushed live). Returns what actually happened, so a filter the hardware cannot express degrades
    /// to a reported fallback rather than silently doing nothing.
    /// </summary>
    DeviceFilterResult SetDeviceFilter(CanIdFilter? filter) => DeviceFilterResult.Unsupported;

    /// <summary>Open <paramref name="device"/> at the given bit rate and start receiving.</summary>
    void Connect(CanDeviceInfo device, CanBitRate bitRate, bool listenOnly = false);

    void Disconnect();

    void Send(uint identifier, bool extended, byte[] data, bool remote = false);

    int StartCyclic(uint identifier, bool extended, byte[] data, bool remote, double intervalMs);

    /// <summary>
    /// Replace the payload of a running cyclic stream in place (same ID/interval), so a value
    /// edited in the UI is picked up without stopping and restarting the stream. No-op if the
    /// handle is unknown (already stopped).
    /// </summary>
    void UpdateCyclic(int handle, byte[] data);

    void StopCyclic(int handle);

    void StopAllCyclic();
}
