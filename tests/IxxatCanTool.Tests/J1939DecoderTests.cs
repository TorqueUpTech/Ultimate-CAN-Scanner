using IxxatCanTool.Can;
using IxxatCanTool.Decoding;
using Xunit;

namespace IxxatCanTool.Tests;

/// <summary>Covers the J1939 PDU1 (peer-to-peer) vs PDU2 (broadcast) split out of a 29-bit ID.</summary>
public class J1939DecoderTests
{
    private static CanFrame Ext(uint id) => new() { Identifier = id, IsExtended = true };

    [Fact]
    public void Pdu2_broadcast_folds_ps_into_pgn_and_has_no_destination()
    {
        // 0x0CF00400 → priority 3, PF 0xF0, PS 0x04, SA 0x00 → PGN 0xF004 (EEC1).
        var info = J1939Decoder.TryDecode(Ext(0x0CF00400));
        Assert.NotNull(info);
        Assert.Equal(3, info!.Priority);
        Assert.Equal(0xF004, info.Pgn);
        Assert.Equal(0x00, info.SourceAddress);
        Assert.Null(info.DestinationAddress);
        Assert.Contains("EEC1", info.PgnName);
    }

    [Fact]
    public void Pdu1_peer_to_peer_uses_ps_as_destination()
    {
        // 0x18EAFF01 → priority 6, PF 0xEA, PS 0xFF (destination), SA 0x01 → PGN 0xEA00 (Request).
        var info = J1939Decoder.TryDecode(Ext(0x18EAFF01));
        Assert.NotNull(info);
        Assert.Equal(6, info!.Priority);
        Assert.Equal(0xEA00, info.Pgn);
        Assert.Equal(0x01, info.SourceAddress);
        Assert.Equal(0xFF, info.DestinationAddress);
        Assert.Equal("Request", info.PgnName);
    }

    [Fact]
    public void Standard_frame_is_not_j1939()
        => Assert.Null(J1939Decoder.TryDecode(new CanFrame { Identifier = 0x7E8, IsExtended = false }));
}
