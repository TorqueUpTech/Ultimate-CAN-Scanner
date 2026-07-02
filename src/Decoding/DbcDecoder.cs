using System.Globalization;
using DbcParserLib;
using DbcParserLib.Model;
using IxxatCanTool.Can;

namespace IxxatCanTool.Decoding;

/// <summary>One numeric signal value decoded from a frame, for charting.</summary>
public readonly record struct DecodedSignal(string Message, string Signal, string Unit, double Value);

/// <summary>
/// Decodes CAN frames against a loaded DBC database. Built once when a file is
/// loaded; <see cref="Decode"/> is then called per-frame on the RX thread.
/// </summary>
public sealed class DbcDecoder
{
    private const uint IdMask = CanFrame.IdentifierMask;

    private readonly Dictionary<(uint Id, bool Extended), Message> _messages;

    public string FilePath { get; }
    public int MessageCount => _messages.Count;

    /// <summary>All messages in the database, for the TX picker.</summary>
    public IReadOnlyList<DbcMessageInfo> Messages { get; }

    private DbcDecoder(string filePath, Dictionary<(uint, bool), Message> messages,
                       IReadOnlyList<DbcMessageInfo> messageList)
    {
        FilePath = filePath;
        _messages = messages;
        Messages = messageList;
    }

    public static DbcDecoder Load(string path)
    {
        Dbc dbc = Parser.ParseFromPath(path);

        var map = new Dictionary<(uint, bool), Message>();
        var list = new List<DbcMessageInfo>();
        foreach (var msg in dbc.Messages)
        {
            map[(msg.ID & IdMask, msg.IsExtID)] = msg; // last definition wins
            list.Add(new DbcMessageInfo(msg));
        }

        return new DbcDecoder(path, map, list);
    }

    /// <summary>
    /// Look up the DBC message for a frame. Some DBCs don't set the extended flag
    /// consistently, so the opposite framing is tried before giving up.
    /// </summary>
    private Message? Match(CanFrame frame)
    {
        uint id = frame.Identifier & IdMask;
        if (_messages.TryGetValue((id, frame.IsExtended), out var msg))
            return msg;
        return _messages.TryGetValue((id, !frame.IsExtended), out msg) ? msg : null;
    }

    /// <summary>Returns a "MsgName: sig=val unit, …" string, or null if no match.</summary>
    public string? Decode(CanFrame frame)
    {
        if (Match(frame) is not { } msg)
            return null;

        byte[] data = frame.Data;
        int? muxValue = TryGetMultiplexorValue(msg, data);

        var parts = new List<string>(msg.Signals.Count);
        foreach (var signal in msg.Signals)
        {
            if (!SignalApplies(signal, muxValue))
                continue;

            string? text = TryDecodeSignal(signal, data);
            if (text is null)
                continue;

            parts.Add(string.IsNullOrEmpty(signal.Unit)
                ? $"{signal.Name}={text}"
                : $"{signal.Name}={text} {signal.Unit}");
        }

        return parts.Count == 0 ? msg.Name : $"{msg.Name}: {string.Join(", ", parts)}";
    }

    /// <summary>
    /// Decode every applicable signal of a frame to its numeric physical value,
    /// for plotting. Value-table (enum) signals are still returned as their raw
    /// numeric value so they can be charted. Empty if the frame has no DBC match.
    /// </summary>
    public IReadOnlyList<DecodedSignal> DecodeSignals(CanFrame frame)
    {
        if (Match(frame) is not { } msg)
            return [];

        byte[] data = frame.Data;
        int? muxValue = TryGetMultiplexorValue(msg, data);

        var result = new List<DecodedSignal>(msg.Signals.Count);
        foreach (var signal in msg.Signals)
        {
            if (!SignalApplies(signal, muxValue))
                continue;
            try
            {
                double value = Packer.RxSignalUnpack(data, signal);
                result.Add(new DecodedSignal(msg.Name, signal.Name, signal.Unit ?? string.Empty, value));
            }
            catch
            {
                // Signal extends past the received data, or other packer error.
            }
        }
        return result;
    }

    private static string? TryDecodeSignal(Signal signal, byte[] data)
    {
        try
        {
            double value = Packer.RxSignalUnpack(data, signal);

            // Prefer a named value-table entry (enum) when one exists.
            if (signal.ValueTableMap is { Count: > 0 } map
                && map.TryGetValue((int)value, out var name))
            {
                return name;
            }

            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }
        catch
        {
            // Signal extends past the received data, or other packer error.
            return null;
        }
    }

    /// <summary>For a multiplexed message, returns the multiplexor switch value.</summary>
    private static int? TryGetMultiplexorValue(Message msg, byte[] data)
    {
        foreach (var signal in msg.Signals)
        {
            if (signal.Multiplexing == "M")
            {
                try { return (int)Packer.RxSignalUnpack(data, signal); }
                catch { return null; }
            }
        }
        return null;
    }

    private static bool SignalApplies(Signal signal, int? muxValue)
    {
        var mux = signal.Multiplexing;
        if (string.IsNullOrEmpty(mux) || mux == "M")
            return true; // plain signal or the multiplexor itself

        // Multiplexed signal "m<N>": only valid when the switch equals N.
        if (muxValue is { } v
            && mux.Length > 1
            && int.TryParse(mux.AsSpan(1), out int group))
        {
            return v == group;
        }
        return false;
    }
}
