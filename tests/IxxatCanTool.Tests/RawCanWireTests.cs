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
}
