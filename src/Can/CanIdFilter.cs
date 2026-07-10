using System.Globalization;

namespace IxxatCanTool.Can;

/// <summary>
/// An immutable CAN-ID acceptance filter for the trace view: a set of inclusive
/// [lo,hi] ID ranges (a single ID is [id,id]) plus an include/exclude mode.
/// Compiled once from user text and read from the RX/playback thread, so it is
/// never mutated after construction.
/// </summary>
public sealed class CanIdFilter
{
    private readonly (uint Lo, uint Hi)[] _ranges;

    /// <summary>False = allowlist (accept only listed IDs); true = blocklist (accept all others).</summary>
    public bool Exclude { get; }

    /// <summary>Number of parsed ID ranges (a bare ID counts as one).</summary>
    public int Count => _ranges.Length;

    private CanIdFilter((uint Lo, uint Hi)[] ranges, bool exclude)
    {
        _ranges = ranges;
        Exclude = exclude;
    }

    /// <summary>True when <paramref name="id"/> should be shown under this filter.</summary>
    public bool Passes(uint id)
    {
        bool match = false;
        foreach (var (lo, hi) in _ranges)
        {
            if (id >= lo && id <= hi)
            {
                match = true;
                break;
            }
        }
        // include (Exclude=false): pass on match; exclude (=true): pass on no match.
        return Exclude ^ match;
    }

    /// <summary>
    /// Parse a "100 7E8 700-7FF" style list into a filter, or null when nothing usable
    /// was entered (caller treats null as "pass everything"). IDs are hex with an optional
    /// "0x" prefix, whitespace/comma/semicolon separated; malformed tokens are skipped so
    /// live typing never throws. IDs are masked to 29 bits to match the arbitration ID.
    /// </summary>
    public static CanIdFilter? Parse(string? text, bool exclude)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var ranges = new List<(uint, uint)>();
        foreach (var token in text.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = token.IndexOf('-', 1); // start at 1: a leading '-' isn't a range separator
            if (dash > 0 && dash < token.Length - 1)
            {
                if (TryParseId(token[..dash], out uint lo) && TryParseId(token[(dash + 1)..], out uint hi))
                    ranges.Add(lo <= hi ? (lo, hi) : (hi, lo));
            }
            else if (TryParseId(token, out uint id))
            {
                ranges.Add((id, id));
            }
        }

        return ranges.Count == 0 ? null : new CanIdFilter(ranges.ToArray(), exclude);
    }

    private static bool TryParseId(string token, out uint id)
    {
        token = token.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (uint.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id))
        {
            id &= CanFrame.IdentifierMask;
            return true;
        }
        return false;
    }
}
