namespace IxxatCanTool.Can;

/// <summary>
/// Outcome of pushing a <see cref="CanIdFilter"/> into an adapter's hardware
/// (<see cref="ICanAdapter.SetDeviceFilter"/>).
///
/// <see cref="Applied"/> false is not an error: the device simply could not express the filter
/// (too many ranges for its slots, or a blocklist), so it was left passing everything and the
/// existing trace-view filter still hides the frames. <see cref="Message"/> says which happened
/// and is written straight to the status line, so the downgrade is never silent.
/// </summary>
/// <param name="Applied">True when the device is now filtering; false when it passes everything.</param>
/// <param name="Message">Human-readable outcome for the status line.</param>
public readonly record struct DeviceFilterResult(bool Applied, string Message)
{
    /// <summary>The backend has no hardware filtering at all.</summary>
    public static DeviceFilterResult Unsupported { get; } =
        new(false, "This adapter does not support device-level filtering.");
}
