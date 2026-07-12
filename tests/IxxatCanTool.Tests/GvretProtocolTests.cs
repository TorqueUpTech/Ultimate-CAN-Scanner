using System.Collections.Generic;
using System.Linq;
using IxxatCanTool.Can;
using IxxatCanTool.Can.Gvret;
using Xunit;

namespace IxxatCanTool.Tests;

/// <summary>
/// Locks the GVRET binary wire format (collin80/GVRET, as SavvyCAN speaks it) so a refactor can't
/// silently break interop: the SETUP_CANBUS/BUILD_CAN_FRAME encoders and the inbound stream parser.
/// </summary>
public class GvretProtocolTests
{
    // ---- Encoders ----

    [Fact]
    public void SetupCanbus_500k_active()
    {
        // 500000 | valid(0x80000000) | enabled(0x40000000) = 0xC007A120, little-endian.
        var b = GvretProtocol.SetupCanbus(500_000, listenOnly: false);
        Assert.Equal(new byte[] { 0xF1, 0x05, 0x20, 0xA1, 0x07, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00 }, b);
    }

    [Fact]
    public void SetupCanbus_listen_only_sets_bit29()
    {
        var b = GvretProtocol.SetupCanbus(500_000, listenOnly: true);
        // + listen-only(0x20000000) -> 0xE007A120
        Assert.Equal(new byte[] { 0xF1, 0x05, 0x20, 0xA1, 0x07, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00 }, b);
    }

    [Fact]
    public void BuildFrame_standard()
    {
        var b = GvretProtocol.BuildFrame(0x123, extended: false, new byte[] { 0x11, 0x22, 0x33 });
        Assert.Equal(new byte[] { 0xF1, 0x00, 0x23, 0x01, 0x00, 0x00, 0x00, 0x03, 0x11, 0x22, 0x33, 0x00 }, b);
    }

    [Fact]
    public void BuildFrame_extended_sets_bit31()
    {
        var b = GvretProtocol.BuildFrame(0x1ABCDEF, extended: true, new byte[] { 0xAA });
        // wire id = 0x81ABCDEF little-endian
        Assert.Equal(new byte[] { 0xF1, 0x00, 0xEF, 0xCD, 0xAB, 0x81, 0x00, 0x01, 0xAA, 0x00 }, b);
    }

    [Fact]
    public void BuildFrame_rejects_over_8_bytes()
    {
        Assert.Throws<System.ArgumentException>(() =>
            GvretProtocol.BuildFrame(0x100, false, new byte[9]));
    }

    // ---- Parser ----

    /// <summary>Synthesize a device-to-PC RX frame: F1 00, micros(4 LE), id(4 LE, bit31 ext), len|bus, data, checksum.</summary>
    private static byte[] RxFrame(uint id, bool ext, byte[] data, uint micros = 0, int bus = 0)
    {
        uint wire = (id & CanFrame.IdentifierMask) | (ext ? 0x8000_0000u : 0);
        var b = new List<byte> { 0xF1, 0x00,
            (byte)micros, (byte)(micros >> 8), (byte)(micros >> 16), (byte)(micros >> 24),
            (byte)wire, (byte)(wire >> 8), (byte)(wire >> 16), (byte)(wire >> 24),
            (byte)((data.Length & 0x0F) | (bus << 4)) };
        b.AddRange(data);
        b.Add(0x00); // checksum byte (device sends 0/xor; parser ignores its value)
        return b.ToArray();
    }

    [Fact]
    public void Parser_reads_standard_frame()
    {
        var p = new GvretFrameParser();
        p.Append(RxFrame(0x1EB, false, new byte[] { 0x01, 0x65 }, micros: 12345, bus: 0));
        Assert.True(p.TryRead(out var f));
        Assert.Equal(0x1EBu, f.Id);
        Assert.False(f.Extended);
        Assert.Equal(new byte[] { 0x01, 0x65 }, f.Data);
        Assert.Equal(12345u, f.TimestampMicros);
        Assert.False(p.TryRead(out _));
    }

    [Fact]
    public void Parser_reads_extended_frame_and_bus()
    {
        var p = new GvretFrameParser();
        p.Append(RxFrame(0x18DAF110, true, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, bus: 1));
        Assert.True(p.TryRead(out var f));
        Assert.Equal(0x18DAF110u, f.Id);
        Assert.True(f.Extended);
        Assert.Equal(1, f.Bus);
        Assert.Equal(4, f.Data.Length);
    }

    [Fact]
    public void Parser_handles_two_frames_in_one_buffer()
    {
        var p = new GvretFrameParser();
        var buf = RxFrame(0x100, false, new byte[] { 1 }).Concat(RxFrame(0x200, false, new byte[] { 2, 3 })).ToArray();
        p.Append(buf);
        Assert.True(p.TryRead(out var a));
        Assert.Equal(0x100u, a.Id);
        Assert.True(p.TryRead(out var b));
        Assert.Equal(0x200u, b.Id);
        Assert.False(p.TryRead(out _));
    }

    [Fact]
    public void Parser_reassembles_across_appends()
    {
        var full = RxFrame(0x321, false, new byte[] { 0xAB, 0xCD });
        var p = new GvretFrameParser();
        p.Append(full[..5]);
        Assert.False(p.TryRead(out _)); // incomplete
        p.Append(full[5..]);
        Assert.True(p.TryRead(out var f));
        Assert.Equal(0x321u, f.Id);
        Assert.Equal(new byte[] { 0xAB, 0xCD }, f.Data);
    }

    [Fact]
    public void Parser_skips_keepalive_reply_before_a_frame()
    {
        // A KEEPALIVE reply (F1 09 DE AD) must be consumed without desyncing the following frame.
        var p = new GvretFrameParser();
        var buf = new byte[] { 0xF1, 0x09, 0xDE, 0xAD }.Concat(RxFrame(0x2A0, false, new byte[] { 0x55 })).ToArray();
        p.Append(buf);
        Assert.True(p.TryRead(out var f));
        Assert.Equal(0x2A0u, f.Id);
    }

    [Fact]
    public void Parser_skips_leading_noise_until_marker()
    {
        var p = new GvretFrameParser();
        var buf = new byte[] { (byte)'O', (byte)'K', (byte)'\r', (byte)'\n' }
            .Concat(RxFrame(0x0C7, false, new byte[] { 0x03, 0xFE })).ToArray();
        p.Append(buf);
        Assert.True(p.TryRead(out var f));
        Assert.Equal(0x0C7u, f.Id);
    }
}
