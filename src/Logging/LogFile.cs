using System.Globalization;
using System.IO;
using System.Text;
using IxxatCanTool.Can;

namespace IxxatCanTool.Logging;

/// <summary>
/// Reads a captured CAN trace back into <see cref="CanFrame"/> records for
/// playback. Auto-detects the layout from the first row:
///
/// <list type="bullet">
/// <item>The <c>Time(s),Dir,ID,Type,DLC,Data</c> header (written by
/// <see cref="TraceLogger"/>) selects the can-trace parser. IDs carry a
/// trailing <c>x</c> for 29-bit frames and data is contiguous hex.</item>
/// <item>The IXXAT canAnalyser3 trace export header
/// (<c>"Bus","No","Time (abs)","State","ID (hex)","DLC","Data (hex)","ASCII"</c>)
/// selects the canAnalyser parser. Fields are double-quoted and the "No" counter
/// carries a thousands-separator comma, so this needs the quote-aware
/// <see cref="SplitCsv"/>.</item>
/// <item>Anything else falls back to the legacy GM log format: ≥7 columns,
/// an <c>HH:MM:SS</c> timestamp in column 2, a hex ID in column 4 and
/// space-separated hex data bytes in column 6.</item>
/// </list>
///
/// This mirrors the importer in the Python DBC-Tool so the two tools read the
/// same files.
/// </summary>
public static class LogFile
{
    private static readonly string[] CanTraceHeader =
        ["time(s)", "dir", "id", "type", "dlc", "data"];

    /// <summary>Read all parseable frames from <paramref name="path"/>.</summary>
    public static IReadOnlyList<CanFrame> Read(string path)
    {
        using var reader = new StreamReader(path);

        string? first = reader.ReadLine();
        if (first is null)
            return [];

        var firstCols = SplitCsv(first);
        bool canTrace = firstCols.Length >= 6
            && firstCols.Take(6)
                        .Select(c => c.Trim().ToLowerInvariant())
                        .SequenceEqual(CanTraceHeader);
        bool canAnalyser = !canTrace && IsCanAnalyserHeader(firstCols);

        var frames = new List<CanFrame>();

        // Only the legacy format is header-less, so its first line is real data.
        if (!canTrace && !canAnalyser)
            TryAdd(frames, ParseLegacy(firstCols));

        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (line.Length == 0)
                continue;
            var cols = SplitCsv(line);
            CanFrame? frame = canTrace ? ParseCanTrace(cols)
                            : canAnalyser ? ParseCanAnalyser(cols)
                            : ParseLegacy(cols);
            TryAdd(frames, frame);
        }

        return frames;
    }

    private static void TryAdd(List<CanFrame> frames, CanFrame? frame)
    {
        if (frame is not null)
            frames.Add(frame);
    }

    /// <summary>Columns: Time(s), Dir, ID[hex, trailing 'x'=extended], Type, DLC, Data[contiguous hex].</summary>
    private static CanFrame? ParseCanTrace(string[] cols)
    {
        if (cols.Length < 6)
            return null;
        try
        {
            double ts = double.Parse(cols[0].Trim(), CultureInfo.InvariantCulture);

            string idText = cols[2].Trim();
            bool extended = idText.EndsWith('x') || idText.EndsWith('X');
            if (extended)
                idText = idText[..^1];
            uint id = Convert.ToUInt32(idText, 16);

            bool remote = cols[3].Trim().Equals("RTR", StringComparison.OrdinalIgnoreCase);
            byte[] data = ParseContiguousHex(cols[5].Trim());

            return new CanFrame
            {
                TimeStamp = ts,
                Direction = ParseDirection(cols[1]),
                Identifier = id,
                IsExtended = extended,
                IsRemote = remote,
                Data = data
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Detect the IXXAT canAnalyser3 trace export header
    /// <c>"Bus","No","Time (abs)","State","ID (hex)","DLC","Data (hex)","ASCII"</c>.
    /// Matched on the three columns we actually read so a "Time (rel)" variant or an
    /// extra trailing column still loads.
    /// </summary>
    private static bool IsCanAnalyserHeader(string[] cols)
    {
        if (cols.Length < 7)
            return false;
        return cols[2].Trim().StartsWith("time", StringComparison.OrdinalIgnoreCase)
            && cols[4].Trim().StartsWith("id", StringComparison.OrdinalIgnoreCase)
            && cols[6].Trim().StartsWith("data", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// IXXAT canAnalyser3 trace export: Time (abs) [HH:MM:SS.fff] in col 2, State in
    /// col 3, ID (hex) in col 4, space-separated hex Data in col 6. Quote-aware
    /// <see cref="SplitCsv"/> has already stripped the surrounding quotes.
    /// </summary>
    private static CanFrame? ParseCanAnalyser(string[] cols)
    {
        if (cols.Length < 7)
            return null;
        try
        {
            double ts = ParseClockSeconds(cols[2].Trim());
            uint id = Convert.ToUInt32(cols[4].Trim(), 16);
            byte[] data = cols[6]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => Convert.ToByte(b, 16))
                .ToArray();

            // The State column flags transmitted frames; everything else is received.
            bool transmitted = cols[3].Trim().StartsWith("T", StringComparison.OrdinalIgnoreCase);

            return new CanFrame
            {
                TimeStamp = ts,
                Direction = transmitted ? CanDirection.Tx : CanDirection.Rx,
                Identifier = id,
                IsExtended = id > 0x7FF,
                IsRemote = false,
                Data = data
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Legacy GM log: HH:MM:SS in col 2, hex ID in col 4, space-separated hex data in col 6.</summary>
    private static CanFrame? ParseLegacy(string[] cols)
    {
        if (cols.Length < 7)
            return null;
        try
        {
            double ts = ParseClockSeconds(cols[2].Trim());
            uint id = Convert.ToUInt32(cols[4].Trim(), 16);
            byte[] data = cols[6]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => Convert.ToByte(b, 16))
                .ToArray();

            return new CanFrame
            {
                TimeStamp = ts,
                Direction = CanDirection.Rx,
                Identifier = id,
                IsExtended = id > 0x7FF,
                IsRemote = false,
                Data = data
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private static CanDirection ParseDirection(string text) =>
        text.Trim().StartsWith("T", StringComparison.OrdinalIgnoreCase)
            ? CanDirection.Tx
            : CanDirection.Rx;

    /// <summary>"HH:MM:SS[.fff]" → seconds since midnight (relative pacing reference).</summary>
    private static double ParseClockSeconds(string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 3)
            throw new FormatException($"Not an HH:MM:SS timestamp: '{text}'");
        double h = double.Parse(parts[0], CultureInfo.InvariantCulture);
        double m = double.Parse(parts[1], CultureInfo.InvariantCulture);
        double s = double.Parse(parts[2], CultureInfo.InvariantCulture);
        return h * 3600 + m * 60 + s;
    }

    private static byte[] ParseContiguousHex(string hex)
    {
        if (hex.Length == 0)
            return [];
        if (hex.Length % 2 != 0)
            throw new FormatException($"Odd-length hex payload: '{hex}'");
        var data = new byte[hex.Length / 2];
        for (int i = 0; i < data.Length; i++)
            data[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return data;
    }

    /// <summary>
    /// Quote-aware CSV split. Honours double-quoted fields so a comma inside quotes
    /// (e.g. the canAnalyser3 "No" counter <c>"174,213"</c>) does not split the row, and
    /// strips the surrounding quotes; a doubled quote (<c>""</c>) inside a field becomes a
    /// literal quote. Unquoted traces (CAN-Tool, legacy GM) split exactly as a plain
    /// comma split would.
    /// </summary>
    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c != '"')
                    field.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"')
                    field.Append(line[++i]); // collapse the escaped "" to one quote
                else
                    inQuotes = false;
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return [.. fields];
    }
}
