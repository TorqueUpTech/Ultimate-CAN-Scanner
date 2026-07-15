namespace IxxatCanTool.Can.Obdx;

/// <summary>
/// Translates a trace-view <see cref="CanIdFilter"/> into OBDX hardware acceptance filters, so the
/// tool drops non-matching frames itself instead of streaming the whole bus across USB/WiFi/BLE.
///
/// The device matches on (ID, mask) pairs — mask bit 1 = "this bit must match", 0 = "don't care" —
/// so an arbitrary inclusive range has to be decomposed into aligned power-of-two blocks, the same
/// shape as CIDR subnetting. A single ID costs one slot (mask = all-ones); a naturally aligned span
/// like 700-7FF also costs one (id 0x700, mask 0x700); an awkward span like 123-456 costs several.
/// When the blocks outrun the device's slots — or the filter is a blocklist, whose PASS/BLOCK
/// precedence the manual never defines — translation fails and the caller falls back to passing
/// everything plus the existing app-side filter.
/// </summary>
public static class ObdxDeviceFilter
{
    // Manual 3.14 Note 1: the default hardware filters allow 28 11-bit and 8 29-bit filters.
    // The Get command is keyed on (number, frame type), so each width has its own number space.
    public const int Max11Bit = 28;
    public const int Max29Bit = 8;

    private const uint Mask11 = 0x7FF;        // every bit of an 11-bit ID must match
    private const uint Mask29 = 0x1FFFFFFF;   // every bit of a 29-bit ID must match
    private const uint StdMax = 0x7FF;        // above this an ID is extended (matches TryParseCanRx)

    /// <summary>Filters that make the tool forward every frame of either width (no device filtering).</summary>
    public static IReadOnlyList<byte[]> PassAll() =>
    [
        ObdxDvi.MonitorAllFilter(extended: false, number: 0),
        ObdxDvi.MonitorAllFilter(extended: true, number: 1),
    ];

    /// <summary>
    /// Translate an allowlist filter into device commands. Returns false (with a reason in
    /// <paramref name="message"/>) when the hardware cannot express it, in which case the caller
    /// should use <see cref="PassAll"/> and keep filtering in the app.
    /// </summary>
    public static bool TryPlan(CanIdFilter filter, out IReadOnlyList<byte[]> commands, out string message)
    {
        commands = [];

        if (filter.Exclude)
        {
            message = "Device filter must be an allowlist — tool left passing every frame. " +
                      "Use the RX ID filter to hide IDs instead.";
            return false;
        }

        var blocks11 = new List<(uint Id, uint Mask)>();
        var blocks29 = new List<(uint Id, uint Mask)>();
        foreach (var (lo, hi) in filter.Ranges)
        {
            // A range may straddle the 11/29-bit boundary; each side maps to its own filter pool.
            if (lo <= StdMax)
                Decompose(lo, Math.Min(hi, StdMax), Mask11, blocks11);
            if (hi > StdMax)
                Decompose(Math.Max(lo, StdMax + 1), hi, Mask29, blocks29);
        }

        if (blocks11.Count == 0 && blocks29.Count == 0)
        {
            message = "Device filter: no valid IDs — leaving the tool passing everything.";
            return false;
        }

        if (blocks11.Count > Max11Bit || blocks29.Count > Max29Bit)
        {
            message = $"Device filter needs {blocks11.Count}x11-bit + {blocks29.Count}x29-bit slots but the " +
                      $"device has {Max11Bit}+{Max29Bit} — tool left passing every frame. " +
                      "Use aligned ranges (e.g. 700-7FF) to spend fewer slots.";
            return false;
        }

        var cmds = new List<byte[]>(blocks11.Count + blocks29.Count);
        byte n = 0;
        foreach (var (id, mask) in blocks11)
            cmds.Add(ObdxDvi.SetEntireFilter(n++, extended: false, ObdxDvi.FilterPass, enabled: true, id, mask));
        n = 0;
        foreach (var (id, mask) in blocks29)
            cmds.Add(ObdxDvi.SetEntireFilter(n++, extended: true, ObdxDvi.FilterPass, enabled: true, id, mask));

        commands = cmds;
        message = $"Device filter active: {blocks11.Count}/{Max11Bit} 11-bit + {blocks29.Count}/{Max29Bit} " +
                  "29-bit slot(s). Non-matching frames never reach the PC (not traced or logged).";
        return true;
    }

    /// <summary>
    /// Split the inclusive range [lo,hi] into the fewest aligned power-of-two blocks, each expressible
    /// as one (id, mask) filter. Greedy: at each step take the largest block that both starts aligned
    /// at <paramref name="lo"/> and still fits under <paramref name="hi"/>.
    /// </summary>
    private static void Decompose(uint lo, uint hi, uint widthMask, List<(uint Id, uint Mask)> into)
    {
        while (lo <= hi)
        {
            uint size = 1;
            while (true)
            {
                uint next = size << 1;
                if (next == 0 || next > widthMask + 1)
                    break;                              // no wider block exists for this ID width
                if ((lo & (next - 1)) != 0)
                    break;                              // lo is not aligned to the bigger block
                if (next - 1 > hi - lo)
                    break;                              // the bigger block would overshoot hi
                size = next;
            }

            var block = (Id: lo, Mask: widthMask & ~(size - 1));
            if (!into.Contains(block))
                into.Add(block);                        // overlapping ranges must not burn two slots

            uint end = lo + size - 1;
            if (end >= hi)
                break;                                  // done (and avoids wrapping past uint.MaxValue)
            lo = end + 1;
        }
    }
}
