using System;
using IxxatCanTool.Can;
using IxxatCanTool.Tcp;
using Xunit;

namespace IxxatCanTool.Tests;

/// <summary>
/// The 13-byte RawCanWire format must stay byte-identical to CAN-Replay and the dash sim:
/// byte 0 = DLC | 0x80 extended flag, bytes 1-4 = big-endian ID, bytes 5-12 = zero-padded data.
/// </summary>
public class RawCanWireTests
{
    [Fact]
    public void Encode_standard_frame()
    {
        var frame = new CanFrame
        {
            Identifier = 0x1ED,
            IsExtended = false,
            Data = Convert.FromHexString("C190050D0538756D")
        };
        Assert.Equal(Convert.FromHexString("08000001EDC190050D0538756D"), RawCanWire.Encode(frame));
    }

    [Fact]
    public void Encode_extended_frame_sets_flag_and_zero_pads()
    {
        var frame = new CanFrame
        {
            Identifier = 0x18FEDF00,
            IsExtended = true,
            Data = new byte[] { 0xAA, 0xBB, 0xCC }
        };
        Assert.Equal(Convert.FromHexString("8318FEDF00AABBCC0000000000"), RawCanWire.Encode(frame));
    }

    [Fact]
    public void Decode_standard_frame_marks_rx_and_trims_to_dlc()
    {
        var f = RawCanWire.Decode(Convert.FromHexString("08000001EDC190050D0538756D"));
        Assert.Equal(CanDirection.Rx, f.Direction);
        Assert.Equal(0x1EDu, f.Identifier);
        Assert.False(f.IsExtended);
        Assert.Equal(Convert.FromHexString("C190050D0538756D"), f.Data);
    }

    [Fact]
    public void Decode_extended_frame_reads_flag_and_dlc()
    {
        var f = RawCanWire.Decode(Convert.FromHexString("8318FEDF00AABBCC0000000000"));
        Assert.True(f.IsExtended);
        Assert.Equal(0x18FEDF00u, f.Identifier);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, f.Data);
    }

    [Fact]
    public void Decode_round_trips_encode()
    {
        var original = new CanFrame
        {
            Identifier = 0x18DAF110,
            IsExtended = true,
            Data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 }
        };
        var f = RawCanWire.Decode(RawCanWire.Encode(original));
        Assert.Equal(original.Identifier, f.Identifier);
        Assert.Equal(original.IsExtended, f.IsExtended);
        Assert.Equal(original.Data, f.Data);
    }

    [Fact]
    public void Decode_rejects_wrong_length()
    {
        Assert.Throws<ArgumentException>(() => RawCanWire.Decode(new byte[12]));
    }
}
