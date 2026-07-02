using System.Globalization;
using IxxatCanTool.Can;

namespace IxxatCanTool.ViewModels;

/// <summary>
/// One displayed row in the trace grid. The cheap columns are formatted once at
/// construction; the expensive <see cref="Decode"/> column is computed lazily on
/// first read and cached — so with a virtualising grid only the ~visible rows are
/// ever decoded, keeping decode work off the RX thread and off dropped rows.
/// </summary>
public sealed class CanFrameRow
{
    private readonly Func<CanFrame, string> _decoder;
    private string? _decode;

    public CanFrameRow(CanFrame frame, Func<CanFrame, string> decoder)
    {
        Frame = frame;
        _decoder = decoder;
        Time = frame.TimeStamp.ToString("F3", CultureInfo.InvariantCulture);
        Direction = frame.Direction.ToString();
        Id = frame.IsExtended ? $"{frame.Identifier:X8}x" : $"{frame.Identifier:X3}";
        Type = frame.IsRemote ? "RTR" : "DATA";
        Dlc = frame.Dlc;
        Data = string.Join(' ', frame.Data.Select(b => b.ToString("X2")));
    }

    public CanFrame Frame { get; }

    public string Time { get; }
    public string Direction { get; }
    public string Id { get; }
    public string Type { get; }
    public int Dlc { get; }
    public string Data { get; }

    public string Decode => _decode ??= _decoder(Frame);
}
