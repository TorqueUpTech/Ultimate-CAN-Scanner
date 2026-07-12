using IxxatCanTool.Can.Gvret;
using IxxatCanTool.Can.J2534;
using IxxatCanTool.Can.Obdx;

namespace IxxatCanTool.Can;

/// <summary>Enumerates and constructs the concrete <see cref="ICanAdapter"/> backends.</summary>
public static class CanAdapters
{
    /// <summary>All adapter kinds, for the UI picker.</summary>
    public static IReadOnlyList<CanAdapterKind> Kinds { get; } = Enum.GetValues<CanAdapterKind>();

    /// <summary>List the devices currently available for the given backend.</summary>
    public static IReadOnlyList<CanDeviceInfo> Enumerate(CanAdapterKind kind) => kind switch
    {
        CanAdapterKind.IxxatVci => CanBusService.EnumerateDevices(),
        CanAdapterKind.Obdx => ObdxCanAdapter.EnumerateDevices(),
        CanAdapterKind.J2534 => J2534CanAdapter.EnumerateDevices(),
        CanAdapterKind.Gvret => GvretCanAdapter.EnumerateDevices(),
        _ => []
    };

    /// <summary>Create a fresh, disconnected adapter instance for the given backend.</summary>
    public static ICanAdapter Create(CanAdapterKind kind) => kind switch
    {
        CanAdapterKind.IxxatVci => new CanBusService(),
        CanAdapterKind.Obdx => new ObdxCanAdapter(),
        CanAdapterKind.J2534 => new J2534CanAdapter(),
        CanAdapterKind.Gvret => new GvretCanAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown adapter kind.")
    };
}
