using System.Collections.Generic;
using System.Linq;
using IxxatCanTool.Can;
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

    // ---- Device-level ID filter translation (ObdxDeviceFilter) ----

    /// <summary>Pull the (id, mask) pair back out of a 0x34/0x00 "entire filter" command.</summary>
    private static (uint Id, uint Mask, bool Extended) DecodeFilter(byte[] cmd)
    {
        uint Be32(int at) => (uint)(cmd[at] << 24 | cmd[at + 1] << 16 | cmd[at + 2] << 8 | cmd[at + 3]);
        return (Be32(7), Be32(11), cmd[4] == 0x01);
    }

    [Fact]
    public void DeviceFilter_maps_single_id_to_one_exact_slot()
    {
        var filter = CanIdFilter.Parse("1E1", exclude: false)!;
        Assert.True(ObdxDeviceFilter.TryPlan(filter, out var cmds, out _));

        byte[] only = Assert.Single(cmds);
        Assert.Equal((0x1E1u, 0x7FFu, false), DecodeFilter(only)); // every bit must match
    }

    [Fact]
    public void DeviceFilter_maps_aligned_range_to_one_slot()
    {
        // 700-7FF is a naturally aligned 256-wide block: id 0x700, mask 0x700 (low 8 bits don't care).
        var filter = CanIdFilter.Parse("700-7FF", exclude: false)!;
        Assert.True(ObdxDeviceFilter.TryPlan(filter, out var cmds, out _));

        byte[] only = Assert.Single(cmds);
        Assert.Equal((0x700u, 0x700u, false), DecodeFilter(only));
    }

    [Theory]
    [InlineData("0C9 1E1 1ED", 3)]
    [InlineData("700-7FF", 1)]
    [InlineData("100-101", 1)]   // aligned pair collapses to one block
    [InlineData("100-102", 2)]   // 100-101 + 102
    public void DeviceFilter_uses_expected_slot_count(string text, int expected)
    {
        var filter = CanIdFilter.Parse(text, exclude: false)!;
        Assert.True(ObdxDeviceFilter.TryPlan(filter, out var cmds, out _));
        Assert.Equal(expected, cmds.Count);
    }

    [Fact]
    public void DeviceFilter_blocks_decompose_to_cover_exactly_the_requested_ids()
    {
        // An awkward range must pass every ID inside it and nothing outside it.
        var filter = CanIdFilter.Parse("123-456", exclude: false)!;
        Assert.True(ObdxDeviceFilter.TryPlan(filter, out var cmds, out _));

        var blocks = cmds.Select(DecodeFilter).ToList();
        bool Accepts(uint id) => blocks.Any(b => (id & b.Mask) == (b.Id & b.Mask));
        for (uint id = 0x120; id <= 0x460; id++)
            Assert.Equal(id >= 0x123 && id <= 0x456, Accepts(id));
    }

    [Fact]
    public void DeviceFilter_splits_a_range_straddling_the_11_and_29_bit_boundary()
    {
        var filter = CanIdFilter.Parse("7F0-810", exclude: false)!;
        Assert.True(ObdxDeviceFilter.TryPlan(filter, out var cmds, out _));

        var blocks = cmds.Select(DecodeFilter).ToList();
        Assert.Contains(blocks, b => !b.Extended); // 7F0-7FF went to the 11-bit pool
        Assert.Contains(blocks, b => b.Extended);  // 800-810 went to the 29-bit pool
    }

    [Fact]
    public void DeviceFilter_rejects_blocklist_so_caller_falls_back_to_app_side()
    {
        var filter = CanIdFilter.Parse("0C9", exclude: true)!;
        Assert.False(ObdxDeviceFilter.TryPlan(filter, out _, out string message));
        Assert.Contains("allowlist", message);
        Assert.Contains("RX ID filter", message); // point the user at the tool that does do blocklists
    }

    [Fact]
    public void DeviceFilter_rejects_a_plan_that_outruns_the_slots()
    {
        // 29 discrete 11-bit IDs need 29 slots; the device has 28.
        string text = string.Join(' ', Enumerable.Range(0x100, 29).Select(i => i.ToString("X3")));
        var filter = CanIdFilter.Parse(text, exclude: false)!;

        Assert.False(ObdxDeviceFilter.TryPlan(filter, out _, out string message));
        Assert.Contains("passing every frame", message);
    }

    [Fact]
    public void RawCanInit_uses_supplied_device_filters_instead_of_pass_all()
    {
        var filter = CanIdFilter.Parse("1E1", exclude: false)!;
        Assert.True(ObdxDeviceFilter.TryPlan(filter, out var cmds, out _));

        var seq = ObdxDvi.RawCanInit(0x06, listenOnly: false, cmds);
        Assert.Contains(seq, c => c.AsSpan().SequenceEqual(cmds[0]));
        Assert.DoesNotContain(seq, c => c.AsSpan().SequenceEqual(ObdxDvi.MonitorAllFilter(false, 0)));
        Assert.True(seq[^1].AsSpan().SequenceEqual(ObdxDvi.SetComms(ObdxDvi.CommsOn))); // enable still last
    }

    [Fact]
    public void SetEntireFilter_matches_manual_hs_can_example()
    {
        // Manual 3.16.3: filter 0, 11-bit, FLOW, enabled, ID 7E8, mask 7FF, flow 7E0.
        Assert.Equal(
            new byte[] { 0x34, 0x11, 0x00, 0x00, 0x00, 0x01, 0x01,
                         0x00, 0x00, 0x07, 0xE8, 0x00, 0x00, 0x07, 0xFF, 0x00, 0x00, 0x07, 0xE0, 0xDC },
            ObdxDvi.SetEntireFilter(0, extended: false, ObdxDvi.FilterFlow, enabled: true,
                                    id: 0x7E8, mask: 0x7FF, flowId: 0x7E0));
    }

    [Fact]
    public void RawCanInit_adds_pass_all_filters_so_frames_actually_flow()
    {
        var seq = ObdxDvi.RawCanInit(0x06, listenOnly: false);
        // Without an enabled PASS filter the OBDX forwards nothing (manual Note 2); both widths covered.
        Assert.Contains(seq, c => c.AsSpan().SequenceEqual(ObdxDvi.MonitorAllFilter(extended: false, number: 0)));
        Assert.Contains(seq, c => c.AsSpan().SequenceEqual(ObdxDvi.MonitorAllFilter(extended: true, number: 1)));
        // Pass-all means mask 0 and filter type PASS (not FLOW — a raw sniffer must not emit flow control).
        var monitor11 = ObdxDvi.MonitorAllFilter(extended: false, number: 0);
        Assert.Equal(ObdxDvi.FilterPass, monitor11[5]); // XX filter-type byte
    }
}
