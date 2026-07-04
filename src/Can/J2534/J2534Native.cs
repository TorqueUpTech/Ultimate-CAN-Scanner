using System.Runtime.InteropServices;
using System.Text;

namespace IxxatCanTool.Can.J2534;

/// <summary>
/// SAE J2534-1 (v04.04) PassThru constants and the <c>PASSTHRU_MSG</c> layout. All J2534
/// integers are Windows <c>unsigned long</c> = 32-bit, so <see cref="uint"/> throughout.
/// </summary>
internal static class J2534Api
{
    // ---- Protocol IDs ----
    public const uint CAN = 5;

    // ---- Message / connect flags (also used in RxStatus) ----
    public const uint CAN_29BIT_ID = 0x00000100; // frame carries a 29-bit extended ID
    public const uint TX_MSG_TYPE = 0x00000001;  // RxStatus: this is an echoed transmit (loopback)

    // ---- Filter types ----
    public const uint PASS_FILTER = 1;

    // ---- Return codes ----
    public const uint STATUS_NOERROR = 0x00;
    public const uint ERR_TIMEOUT = 0x09;
    public const uint ERR_BUFFER_EMPTY = 0x10;

    /// <summary>Max payload of a <c>PASSTHRU_MSG</c> per the J2534 spec.</summary>
    public const int MaxDataSize = 4128;
}

/// <summary>
/// Managed mirror of the J2534 <c>PASSTHRU_MSG</c> (used for TX and filter setup, where the
/// per-call cost of marshalling the fixed 4128-byte buffer is irrelevant). The RX hot path
/// reads fields straight out of an unmanaged buffer instead — see <c>J2534CanAdapter</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PassThruMsg
{
    public uint ProtocolID;
    public uint RxStatus;
    public uint TxFlags;
    public uint Timestamp;
    public uint DataSize;
    public uint ExtraDataIndex;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = J2534Api.MaxDataSize)]
    public byte[] Data;

    // Byte offsets of the header fields within the unmanaged struct (default 4-byte packing,
    // no padding since every field is a 4-byte uint). Used by the RX manual reader.
    public const int RxStatusOffset = 4;
    public const int DataSizeOffset = 16;
    public const int DataOffset = 24;

    public static PassThruMsg Empty() => new() { Data = new byte[J2534Api.MaxDataSize] };
}

// ---- PassThru entry points. J2534 is WINAPI (__stdcall on x86; ignored on x64). ----

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate uint PassThruOpen(IntPtr pName, out uint deviceId);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate uint PassThruClose(uint deviceId);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate uint PassThruConnect(uint deviceId, uint protocolId, uint flags, uint baudRate, out uint channelId);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate uint PassThruDisconnect(uint channelId);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate uint PassThruReadMsgs(uint channelId, IntPtr pMsg, ref uint numMsgs, uint timeoutMs);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate uint PassThruWriteMsgs(uint channelId, ref PassThruMsg pMsg, ref uint numMsgs, uint timeoutMs);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate uint PassThruStartMsgFilter(
    uint channelId, uint filterType, ref PassThruMsg maskMsg, ref PassThruMsg patternMsg,
    IntPtr flowControlMsg, out uint filterId);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate uint PassThruGetLastError(StringBuilder errorDescription);

/// <summary>
/// Loads a vendor J2534 DLL by path and binds the PassThru entry points we use. Loading a
/// 32-bit DLL from this x64 process throws <see cref="BadImageFormatException"/> (surfaced with
/// a friendly message by the adapter).
/// </summary>
internal sealed class J2534Library : IDisposable
{
    private IntPtr _handle;

    public PassThruOpen Open { get; }
    public PassThruClose Close { get; }
    public PassThruConnect Connect { get; }
    public PassThruDisconnect Disconnect { get; }
    public PassThruReadMsgs ReadMsgs { get; }
    public PassThruWriteMsgs WriteMsgs { get; }
    public PassThruStartMsgFilter StartMsgFilter { get; }
    public PassThruGetLastError GetLastError { get; }

    public J2534Library(string dllPath)
    {
        _handle = NativeLibrary.Load(dllPath);
        try
        {
            Open = Bind<PassThruOpen>("PassThruOpen");
            Close = Bind<PassThruClose>("PassThruClose");
            Connect = Bind<PassThruConnect>("PassThruConnect");
            Disconnect = Bind<PassThruDisconnect>("PassThruDisconnect");
            ReadMsgs = Bind<PassThruReadMsgs>("PassThruReadMsgs");
            WriteMsgs = Bind<PassThruWriteMsgs>("PassThruWriteMsgs");
            StartMsgFilter = Bind<PassThruStartMsgFilter>("PassThruStartMsgFilter");
            GetLastError = Bind<PassThruGetLastError>("PassThruGetLastError");
        }
        catch
        {
            NativeLibrary.Free(_handle);
            _handle = IntPtr.Zero;
            throw;
        }
    }

    private T Bind<T>(string export) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_handle, export));

    /// <summary>Fetch the driver's last-error text (only meaningful right after a failed call).</summary>
    public string LastError()
    {
        try
        {
            var sb = new StringBuilder(80);
            GetLastError(sb);
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeLibrary.Free(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
