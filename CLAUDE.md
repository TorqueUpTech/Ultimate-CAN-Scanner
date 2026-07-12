# CAN-Tool — Claude Code notes

> WPF/.NET 8 CAN tool: live RX trace, DBC + J1939 decode, frame/signal TX, multi-signal
> TX list, log playback (offline / onto the bus / over TCP), ScottPlot graph, live gauges.
> Four runtime-selectable adapter backends behind `ICanAdapter`: **Ixxat VCI4**
> (`CanBusService`), **OBDX Pro** (`src/Can/Obdx/`, DVI protocol; USB / WiFi / BLE),
> **J2534** (`src/Can/J2534/`, PassThru DLL), and **GVRET / ESP32RET**
> (`src/Can/Gvret/`, GVRET binary protocol; USB serial / WiFi TCP).
> Project specifics live in `README.md`.

**Shared CAN reference:** the global `~/.claude/CLAUDE.md` holds the cross-project CAN
facts — vehicles (Patrol Y61, GM modules), the 500 kbit/s 11-bit GM bus, IXXAT/ESP32
adapters, the CSV trace formats, the signal reverse-engineering workflow, and the
**DBC → Vector CANdb++** rule. Read it first.

This project owns the canonical **CSV trace format** (`Time(s),Dir,ID,Type,DLC,Data`) and
the signal-RE skill `.claude/skills/decode-can-signal-from-logs/` — the authoritative
how-to for decoding/calibrating a signal from logs.
