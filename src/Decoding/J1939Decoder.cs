using IxxatCanTool.Can;

namespace IxxatCanTool.Decoding;

/// <summary>Result of decoding a 29-bit J1939 identifier.</summary>
public sealed record J1939Info(
    int Priority,
    int Pgn,
    int SourceAddress,
    int? DestinationAddress,
    string PgnName)
{
    public string Summary =>
        DestinationAddress is { } da
            ? $"PGN {Pgn} ({PgnName})  SA {SourceAddress}  DA {da}  Pri {Priority}"
            : $"PGN {Pgn} ({PgnName})  SA {SourceAddress}  Pri {Priority}";
}

/// <summary>
/// Decodes the J1939 transport fields out of a 29-bit CAN identifier. This is
/// the protocol layer most commonly seen on a USB-to-CAN automotive adapter.
/// </summary>
public static class J1939Decoder
{
    // A small, extensible table of well-known PGNs. Extend as needed.
    private static readonly IReadOnlyDictionary<int, string> KnownPgns = new Dictionary<int, string>
    {
        [0xF004] = "EEC1 – Electronic Engine Controller 1",
        [0xFEF1] = "CCVS – Cruise Control / Vehicle Speed",
        [0xFEEE] = "ET1 – Engine Temperature 1",
        [0xFEF6] = "IC1 – Inlet/Exhaust Conditions 1",
        [0xFEEC] = "VI – Vehicle Identification",
        [0xFEE5] = "HOURS – Engine Hours/Revolutions",
        [0xFEF2] = "LFE – Fuel Economy",
        [0xFEF5] = "AMB – Ambient Conditions",
        [0xFD7C] = "DPF – Diesel Particulate Filter Control 1",
        [0xEA00] = "Request",
        [0xEB00] = "TP.DT – Transport Protocol Data Transfer",
        [0xEC00] = "TP.CM – Transport Protocol Connection Mgmt",
        [0xEE00] = "Address Claimed",
        [0xFECA] = "DM1 – Active Diagnostic Trouble Codes",
        [0xFECB] = "DM2 – Previously Active DTCs",
    };

    /// <summary>
    /// Returns J1939 fields for an extended frame, or <c>null</c> for 11-bit
    /// frames (which are not J1939).
    /// </summary>
    public static J1939Info? TryDecode(CanFrame frame)
    {
        if (!frame.IsExtended)
            return null;

        uint id = frame.Identifier & 0x1FFFFFFF;

        int priority = (int)((id >> 26) & 0x7);
        int extDataPage = (int)((id >> 25) & 0x1);
        int dataPage = (int)((id >> 24) & 0x1);
        int pduFormat = (int)((id >> 16) & 0xFF);
        int pduSpecific = (int)((id >> 8) & 0xFF);
        int sourceAddress = (int)(id & 0xFF);

        int pageBits = (extDataPage << 17) | (dataPage << 16);

        int pgn;
        int? destination;
        if (pduFormat < 0xF0)
        {
            // PDU1: peer-to-peer, PS byte is the destination address (not part of PGN).
            pgn = pageBits | (pduFormat << 8);
            destination = pduSpecific;
        }
        else
        {
            // PDU2: broadcast, PS byte is the group extension (part of PGN).
            pgn = pageBits | (pduFormat << 8) | pduSpecific;
            destination = null;
        }

        string name = KnownPgns.TryGetValue(pgn, out var n) ? n : "unknown";
        return new J1939Info(priority, pgn, sourceAddress, destination, name);
    }
}
