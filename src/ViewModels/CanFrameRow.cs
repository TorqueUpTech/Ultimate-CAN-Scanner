using IxxatCanTool.Can;

namespace IxxatCanTool.ViewModels;

/// <summary>One displayed row in the trace grid.</summary>
public sealed class CanFrameRow
{
    public CanFrameRow(CanFrame frame, string decode)
    {
        Frame = frame;
        Decode = decode;
    }

    public CanFrame Frame { get; }

    public string Time => Frame.TimeStamp.ToString("F3");
    public string Direction => Frame.Direction.ToString();
    public string Id => Frame.IsExtended ? $"{Frame.Identifier:X8}x" : $"{Frame.Identifier:X3}";
    public string Type => Frame.IsRemote ? "RTR" : "DATA";
    public int Dlc => Frame.Dlc;
    public string Data => string.Join(' ', Frame.Data.Select(b => b.ToString("X2")));
    public string Decode { get; }
}
