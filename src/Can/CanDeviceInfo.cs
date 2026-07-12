namespace IxxatCanTool.Can;

/// <summary>Which backend a <see cref="CanDeviceInfo"/> belongs to and opens with.</summary>
public enum CanAdapterKind
{
    /// <summary>Ixxat VCI4 (USB-to-CAN V2).</summary>
    IxxatVci,
    /// <summary>OBDX Pro scantool (USB / WiFi / BLE) over the DVI protocol.</summary>
    Obdx,
    /// <summary>Any SAE J2534-1 (v04.04) PassThru device via its installed vendor DLL.</summary>
    J2534,
    /// <summary>GVRET / ESP32RET device (SavvyCAN-compatible) over the GVRET binary protocol (USB / WiFi).</summary>
    Gvret
}

/// <summary>
/// UI-friendly description of a selectable CAN device. <see cref="Key"/> is an opaque,
/// adapter-specific handle used to actually open it: for Ixxat it is the VCI object id;
/// for OBDX it is a transport key such as <c>serial:COM5</c> or <c>tcp:192.168.4.1:35000</c>;
/// for J2534 it is the vendor driver DLL path.
/// </summary>
public sealed record CanDeviceInfo(
    CanAdapterKind Adapter,
    string Key,
    string Description,
    string Detail)
{
    public override string ToString() =>
        string.IsNullOrEmpty(Detail) ? Description : $"{Description} [{Detail}]";
}
