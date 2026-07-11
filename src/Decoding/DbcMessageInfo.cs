using DbcParserLib;
using DbcParserLib.Model;
using IxxatCanTool.Can;

namespace IxxatCanTool.Decoding;

/// <summary>UI-facing wrapper over a DBC message. Hides the DbcParserLib types.</summary>
public sealed class DbcMessageInfo
{
    private const uint IdMask = CanFrame.IdentifierMask;

    internal Message Message { get; }

    internal DbcMessageInfo(Message message)
    {
        Message = message;
        Signals = message.Signals.Select(s => new DbcSignalInfo(s, this)).ToList();
    }

    public string Name => Message.Name;
    public uint Id => Message.ID & IdMask;
    public bool Extended => Message.IsExtID;
    public int Dlc => Message.DLC;
    public IReadOnlyList<DbcSignalInfo> Signals { get; }

    public string Display =>
        Extended ? $"{Name}  (0x{Id:X8}x)" : $"{Name}  (0x{Id:X3})";

    /// <summary>
    /// Encode several signals of this message into one frame payload sized to the DLC.
    /// Signals not supplied are left at zero; later entries for the same bits win.
    /// </summary>
    public byte[] EncodeSignals(IEnumerable<(DbcSignalInfo signal, double value)> values)
    {
        var data = new byte[Math.Clamp(Dlc, 1, 8)];
        foreach (var (signal, value) in values)
            Packer.TxSignalPack(data, value, signal.Signal);
        return data;
    }
}

/// <summary>UI-facing wrapper over a DBC signal, with TX encoding.</summary>
public sealed class DbcSignalInfo
{
    internal Signal Signal { get; }
    public DbcMessageInfo Message { get; }

    internal DbcSignalInfo(Signal signal, DbcMessageInfo message)
    {
        Signal = signal;
        Message = message;
    }

    public string Name => Signal.Name;
    public string Unit => Signal.Unit ?? string.Empty;
    public double Minimum => Signal.Minimum;
    public double Maximum => Signal.Maximum;
    public double Factor => Signal.Factor;
    public int Length => Signal.Length;

    public string Display => string.IsNullOrEmpty(Unit) ? Name : $"{Name} [{Unit}]";

    // GM rolling/alive-counter name conventions (case-insensitive suffixes): e.g.
    // StWhlAngAliveRollCnt, BrkPedPosAlvRolngCnt, ACCCmndAlvRlgCnt, ETC_FrmCntr, TCAliveRC,
    // CmndAxlTrqARC. The bare "rc" suffix catches the AliveRC/ARC family but would also hit
    // "...Src" (Source), so that's excluded.
    private static readonly string[] CounterSuffixes =
    [
        "rollcnt", "rolngcnt", "rlgcnt", "rollgcnt", "rollingcnt", "rollcount",
        "rollingcount", "alivecnt", "alivecount", "msgcnt", "cntr", "counter"
    ];

    /// <summary>
    /// Best-effort guess that this signal is a rolling/alive counter, from its DBC name.
    /// Used to pre-tick the TX-list "Roll" box; the user can override either way.
    /// </summary>
    public bool IsLikelyRollingCounter => IsRollingCounterName(Name);

    /// <summary>Name-only heuristic for a rolling/alive counter (see <see cref="CounterSuffixes"/>).</summary>
    public static bool IsRollingCounterName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        string n = name.ToLowerInvariant();
        if (n.EndsWith("src", StringComparison.Ordinal))
            return false; // "…Src" (Source), not a counter
        if (n.EndsWith("rc", StringComparison.Ordinal))
            return true;  // AliveRC / ARC / AlRC family
        foreach (var s in CounterSuffixes)
            if (n.EndsWith(s, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Encode <paramref name="value"/> (a physical value) into a fresh frame
    /// payload sized to the message DLC. Other signals are left at zero.
    /// </summary>
    public byte[] Encode(double value)
    {
        var data = new byte[Math.Clamp(Message.Dlc, 1, 8)];
        Packer.TxSignalPack(data, value, Signal);
        return data;
    }
}
