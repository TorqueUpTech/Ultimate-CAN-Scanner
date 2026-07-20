using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IxxatCanTool.Can;
using IxxatCanTool.Decoding;
using IxxatCanTool.ViewModels;
using Xunit;

namespace IxxatCanTool.Tests;

/// <summary>
/// Covers the "only signals with data in log" gauge filter. The load-bearing risk is key
/// alignment: the filter matches each <see cref="LiveSignal"/> against the set of
/// "Message|Signal" keys produced by <see cref="DbcDecoder.DecodeSignals"/>. If those two
/// name sources ever diverged, every signal would look absent and the filter would hide
/// everything — so this exercises the real decoder against real <see cref="LiveSignal"/>/
/// <see cref="LiveMessageGroup"/> objects built exactly as the view model builds them.
/// </summary>
public class LiveGaugeFilterTests
{
    // 0x100 MSG_A (two signals), 0x200 MSG_B (one signal).
    private const string Dbc =
        "VERSION \"\"\n\nNS_ :\n\nBS_:\n\nBU_: ECU\n\n" +
        "BO_ 256 MSG_A: 8 ECU\n" +
        " SG_ SigA1 : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n" +
        " SG_ SigA2 : 8|8@1+ (1,0) [0|255] \"\" Vector__XXX\n\n" +
        "BO_ 512 MSG_B: 8 ECU\n" +
        " SG_ SigB1 : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n";

    private static DbcDecoder LoadDbc()
    {
        string path = Path.Combine(Path.GetTempPath(), "can-tool-test-" + Guid.NewGuid().ToString("N") + ".dbc");
        File.WriteAllText(path, Dbc);
        try { return DbcDecoder.Load(path); }
        finally { File.Delete(path); }
    }

    // Mirror of MainViewModel.BuildCanIdGroups: one group per message, one LiveSignal per signal.
    private static List<LiveMessageGroup> BuildGroups(DbcDecoder dbc) =>
        dbc.Messages.Select(msg =>
        {
            var signals = msg.Signals
                .Select(s => new LiveSignal(msg.Name, s.Name, s.Unit, s.Minimum, s.Maximum))
                .ToList();
            string idText = msg.Extended ? $"0x{msg.Id:X8}x" : $"0x{msg.Id:X3}";
            return new LiveMessageGroup(msg.Id, idText, msg.Name, signals);
        }).ToList();

    // Mirror of MainViewModel.ComputeLoggedSignalKeys (decode path) + ApplyLogDataFilter.
    private static void ApplyFilter(IEnumerable<LiveMessageGroup> groups, DbcDecoder dbc,
                                    IReadOnlyList<CanFrame> log, bool filtering)
    {
        HashSet<string>? logged = null;
        if (filtering)
        {
            logged = new HashSet<string>();
            foreach (var frame in log)
                foreach (var s in dbc.DecodeSignals(frame))
                    logged.Add(s.Message + "|" + s.Signal);
        }

        foreach (var group in groups)
        {
            foreach (var sig in group.Signals)
            {
                bool has = logged is null || logged.Contains(sig.MessageName + "|" + sig.SignalName);
                sig.HasLogData = has;
                sig.IsVisible = !filtering || has;
            }
            group.IsVisible = !filtering || group.HasLogData;
        }
    }

    private static CanFrame Frame(uint id) =>
        new() { Identifier = id, Data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } };

    [Fact]
    public void Decoded_keys_match_live_signal_keys()
    {
        // The alignment guarantee itself: every signal a frame decodes to corresponds to a
        // LiveSignal with the identical (MessageName, SignalName).
        var dbc = LoadDbc();
        var groups = BuildGroups(dbc);
        var liveKeys = groups.SelectMany(g => g.Signals)
                             .Select(s => s.MessageName + "|" + s.SignalName)
                             .ToHashSet();

        var decoded = dbc.DecodeSignals(Frame(0x100));
        Assert.NotEmpty(decoded);
        foreach (var d in decoded)
            Assert.Contains(d.Message + "|" + d.Signal, liveKeys);
    }

    [Fact]
    public void Filter_hides_signals_and_ids_absent_from_log()
    {
        var dbc = LoadDbc();
        var groups = BuildGroups(dbc);
        var log = new[] { Frame(0x100), Frame(0x100) }; // MSG_A present, MSG_B absent

        ApplyFilter(groups, dbc, log, filtering: true);

        var a = groups.Single(g => g.Name == "MSG_A");
        var b = groups.Single(g => g.Name == "MSG_B");

        Assert.True(a.IsVisible);
        Assert.All(a.Signals, s => Assert.True(s.IsVisible && s.HasLogData));

        Assert.False(b.IsVisible);
        Assert.All(b.Signals, s => Assert.False(s.IsVisible || s.HasLogData));
    }

    [Fact]
    public void Filter_off_shows_everything()
    {
        var dbc = LoadDbc();
        var groups = BuildGroups(dbc);
        var log = new[] { Frame(0x100) };

        ApplyFilter(groups, dbc, log, filtering: false);

        Assert.All(groups, g => Assert.True(g.IsVisible));
        Assert.All(groups.SelectMany(g => g.Signals), s => Assert.True(s.IsVisible));
    }

    [Fact]
    public void Gauge_selection_requires_both_selected_and_visible()
    {
        // RebuildGauges adds a signal only when IsSelected && IsVisible.
        var dbc = LoadDbc();
        var groups = BuildGroups(dbc);
        ApplyFilter(groups, dbc, new[] { Frame(0x100) }, filtering: true);

        foreach (var g in groups) g.IsEnabled = true;
        var gauges = groups.Where(g => g.IsEnabled)
                           .SelectMany(g => g.Signals)
                           .Where(s => s.IsSelected && s.IsVisible)
                           .ToList();

        Assert.All(gauges, s => Assert.Equal("MSG_A", s.MessageName)); // MSG_B hidden -> no gauges
        Assert.Equal(2, gauges.Count);
    }
}
