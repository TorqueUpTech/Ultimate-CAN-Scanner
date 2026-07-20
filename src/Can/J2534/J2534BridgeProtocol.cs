using System.IO;
using System.Text;

namespace IxxatCanTool.Can.J2534;

/// <summary>
/// The tiny framed protocol spoken between the x64 app (<c>J2534BridgeSession</c>) and the x86 host
/// process over the child's redirected stdin/stdout. Each message is
/// <c>[1-byte type][4-byte big-endian length][payload]</c>. Using length-prefixed binary over the
/// stdio pipe keeps the bridge free of TCP ports/firewall prompts and ties its lifetime to the
/// child process. The host writes to <b>stdout</b> and reads TX from <b>stdin</b>; it keeps stdout
/// strictly to this protocol and uses stderr for any human-readable diagnostics.
/// </summary>
internal static class J2534BridgeProtocol
{
    public const byte Ready = 0x01;  // host -> app: channel open, streaming (no payload)
    public const byte Error = 0x02;  // host -> app: fatal error (payload = UTF-8 text)
    public const byte Frame = 0x03;  // both ways: payload = 13-byte RawCanWire frame

    public static void WriteReady(Stream s) => Write(s, Ready, []);

    public static void WriteError(Stream s, string message) => Write(s, Error, Encoding.UTF8.GetBytes(message));

    public static void WriteFrame(Stream s, byte[] wire13) => Write(s, Frame, wire13);

    private static void Write(Stream s, byte type, byte[] payload)
    {
        var header = new byte[5];
        header[0] = type;
        header[1] = (byte)(payload.Length >> 24);
        header[2] = (byte)(payload.Length >> 16);
        header[3] = (byte)(payload.Length >> 8);
        header[4] = (byte)payload.Length;
        lock (s)
        {
            s.Write(header, 0, header.Length);
            if (payload.Length > 0)
                s.Write(payload, 0, payload.Length);
            s.Flush();
        }
    }

    /// <summary>
    /// Read one message, blocking until it arrives. Returns null at end of stream (peer closed).
    /// Throws <see cref="EndOfStreamException"/> only on a truncated message mid-frame.
    /// </summary>
    public static (byte Type, byte[] Payload)? Read(Stream s)
    {
        var header = new byte[5];
        int got = ReadFull(s, header, 0, 5, allowEof: true);
        if (got == 0)
            return null; // clean EOF at a message boundary

        int len = (header[1] << 24) | (header[2] << 16) | (header[3] << 8) | header[4];
        var payload = new byte[len];
        if (len > 0)
            ReadFull(s, payload, 0, len, allowEof: false);
        return (header[0], payload);
    }

    // Read exactly count bytes. With allowEof, a clean EOF before any byte returns 0 (no more
    // messages); a partial read always throws (a message was cut off).
    private static int ReadFull(Stream s, byte[] buffer, int offset, int count, bool allowEof)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buffer, offset + total, count - total);
            if (n == 0)
            {
                if (total == 0 && allowEof)
                    return 0;
                throw new EndOfStreamException("Bridge stream ended mid-message.");
            }
            total += n;
        }
        return total;
    }
}
