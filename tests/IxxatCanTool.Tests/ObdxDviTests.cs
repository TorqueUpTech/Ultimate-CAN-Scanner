using System.Collections.Generic;
using IxxatCanTool.Can.Obdx;
using Xunit;

namespace IxxatCanTool.Tests;

/// <summary>
/// Locks the OBDX Pro DVI codec to the vendor manual's worked hex — the checksums
/// (DC / C7 / D2), the exact command frames (31 02 01 02 C9, etc.), and the streaming
/// parser (small / large / split / resync). Ports the throwaway harness used during the
/// OBDX integration into a permanent regression test.
/// </summary>
public class ObdxDviTests
{
    [Theory]
    [InlineData(new byte[] { 0x22, 0x01, 0x00 }, 0xDC)]
    [InlineData(new byte[] { 0x32, 0x05, 0x00, 0x01, 0x00, 0x00, 0x00 }, 0xC7)]
    [InlineData(new byte[] { 0x32, 0x05, 0x00, 0xFF, 0xFE, 0xFC, 0xFD }, 0xD2)]
    public void Checksum_matches_manual(byte[] body, byte expected)
        => Assert.Equal(expected, ObdxDvi.Checksum(body));

    [Fact]
    public void Command_builders_match_manual()
    {
        Assert.Equal(new byte[] { 0x31, 0x02, 0x01, 0x02, 0xC9 }, ObdxDvi.SetProtocol(ObdxDvi.ProtoHsCan));
        Assert.Equal(new byte[] { 0x31, 0x02, 0x02, 0x01, 0xC9 }, ObdxDvi.SetComms(ObdxDvi.CommsOn));
        Assert.Equal(new byte[] { 0x34, 0x01, 0x24, 0xA6 }, ObdxDvi.ClearAllFilters());
        Assert.Equal(new byte[] { 0x34, 0x02, 0x15, 0x06, 0xAE }, ObdxDvi.SetCanBaud(0x06));
        Assert.Equal(new byte[] { 0x10, 0x05, 0x00, 0x00, 0x07, 0xE0, 0x20, 0xE3 },
                     ObdxDvi.SendCanFrame(0x7E0, new byte[] { 0x20 }));
    }

    [Fact]
    public void Parser_reads_small_frame()
    {
        var p = new ObdxFrameParser();
        p.Append(ObdxDvi.Command(0x08, new byte[] { 0x00, 0x00, 0x07, 0xE8, 0x04, 0x41, 0x0C, 0x09, 0xC4 }));

        Assert.True(p.TryRead(out var m));
        Assert.Equal(0x08, m.Command);
        Assert.True(ObdxDvi.TryParseCanRx(m, out uint id, out bool ext, out var data));
        Assert.Equal(0x7E8u, id);
        Assert.False(ext);
        Assert.Equal(new byte[] { 0x04, 0x41, 0x0C, 0x09, 0xC4 }, data);
        Assert.False(p.TryRead(out _));
    }

    [Fact]
    public void Parser_reads_large_frame_with_16bit_length()
    {
        // 0x09 uses a two-byte length; same logical frame as the small case.
        var body = new List<byte> { 0x09, 0x00, 0x09, 0x00, 0x00, 0x07, 0xE8, 0x04, 0x41, 0x0C, 0x09, 0xC4 };
        body.Add(ObdxDvi.Checksum(body.ToArray()));

        var p = new ObdxFrameParser();
        p.Append(body.ToArray());

        Assert.True(p.TryRead(out var m));
        Assert.Equal(0x09, m.Command);
        Assert.True(ObdxDvi.TryParseCanRx(m, out uint id, out _, out var data));
        Assert.Equal(0x7E8u, id);
        Assert.Equal(new byte[] { 0x04, 0x41, 0x0C, 0x09, 0xC4 }, data);
    }

    [Fact]
    public void Parser_handles_split_and_interleaved()
    {
        var ack = new byte[] { 0x20, 0x01, 0x00, 0xDE };
        var rx = ObdxDvi.Command(0x08, new byte[] { 0x00, 0x00, 0x01, 0xED, 0xC1, 0x90 });

        var p = new ObdxFrameParser();
        p.Append(ack[..3]);                 // partial ack — nothing complete yet
        Assert.False(p.TryRead(out _));
        p.Append(ack[3..]);                 // rest of ack
        p.Append(rx);                       // then a full CAN frame

        Assert.True(p.TryRead(out var a));
        Assert.Equal(0x20, a.Command);
        Assert.True(p.TryRead(out var b));
        Assert.Equal(0x08, b.Command);
        Assert.True(ObdxDvi.TryParseCanRx(b, out uint id, out _, out _));
        Assert.Equal(0x1EDu, id);
    }

    [Fact]
    public void RawCanInit_disables_iso_tp_and_write_formatting_then_enables_comms()
    {
        var seq = ObdxDvi.RawCanInit(0x06, listenOnly: false);
        Assert.Contains(seq, c => c.AsSpan().SequenceEqual(ObdxDvi.SetAutoProcessing(false)));
        Assert.Contains(seq, c => c.AsSpan().SequenceEqual(ObdxDvi.SetAutoFormatting(false)));
        Assert.True(seq[^1].AsSpan().SequenceEqual(ObdxDvi.SetComms(ObdxDvi.CommsOn))); // enable is last
    }
}
