---
name: replay-and-graph-logs
description: >
  Use CAN-Tool's log playback (View / Replay-to-grid / Replay-to-bus) and the
  ScottPlot graph view to inspect or re-emit a captured CSV trace. Use when the
  user wants to play back a log, replay frames onto the bus, plot decoded signals
  over time, watch a live sweeping chart, or eyeball a candidate signal/calibration.
  Covers the three modes, the DBC requirement for graphing, and the live-sweep gotcha.
---

# Replaying & graphing a captured log

CAN-Tool can open a captured CSV trace and either re-view it, re-time it into the
grid, or re-transmit it on the bus — and plot the decoded signals. Code lives in
`src/ViewModels/MainViewModel.cs` (playback + `PlotSignals`), `MainWindow.xaml(.cs)`
(PLAYBACK bar + graph panel), `src/Logging/LogFile.cs` (reader),
`src/ViewModels/PlotSignal.cs` (one plottable series).

## Three playback modes (PLAYBACK bar → Mode)

| Mode | What it does |
|---|---|
| **View (load all)** | Dumps every decoded frame into the grid at once. Fast scan; grid keeps the most recent ~5000 rows. |
| **Replay → grid** | Re-feeds frames into the grid at recorded inter-frame timing ÷ speed. Offline; no bus needed. |
| **Replay → bus** | Re-transmits via the normal `Send` path (needs a connected adapter). Frames echo back into the grid via self-reception and are logged if a trace log is running. |
| **Replay → TCP** | Broadcasts each frame over TCP in the 13-byte RawCanWire format (`src/Tcp/`) instead of onto the bus — feeds the Can-Display dash sim with no adapter (same wire + `127.0.0.1:51729` as CAN-Replay). Needs no DBC and no adapter. Mirrored into the grid for feedback. |

### TCP broadcast (Replay → TCP)

The **TCP Server** toggle in the playback bar starts/stops the loopback listener on
`127.0.0.1:51729`; the status text shows the attached client count. Workflow: toggle
**TCP Server** on → start the dash sim (it connects as a client) → **Mode: Replay → TCP**
→ **Play**. Pressing Play in this mode also auto-starts the server if it's off, and the
server stays up across passes (only the toggle or app exit stops it) so a connected
client isn't dropped between plays. Wire layout: byte 0 = DLC | `0x80` extended flag,
bytes 1-4 = 32-bit big-endian ID, bytes 5-12 = zero-padded data — `src/Tcp/RawCanWire.cs`.

Speed is a multiplier (`0` = as fast as possible). Play/Pause/Stop. **Loop** (toggle)
repeats the run from the first frame until Stop — handy for feeding the dash sim a short
capture continuously over *Replay → TCP*. It's read at the end of each pass, so toggling
it mid-run takes effect after the current pass; *View* ignores it (instant dump). Pacing
uses `Task.Delay`, whose ~15 ms Windows resolution makes very tight traces play slightly
coarser than recorded.

## Steps

1. **Open Log…** → pick the CSV. Auto-detects the layout: can-trace
   (`Time(s),Dir,ID,Type,DLC,Data`), the quoted IXXAT **canAnalyser3** trace export
   (`"Bus","No","Time (abs)",…` — double-quoted fields, comma-in-quotes "No" counter,
   handled by the quote-aware CSV split), or the legacy GM format (`HH:MM:SS` +
   space-separated hex). If **Play stays greyed out**, the file parsed to 0 frames —
   the status bar reads "contained no parseable frames"; check it matches one of these.
2. To decode/plot, **Load DBC…** first — without a DBC, frames show raw and the
   graph has no signals.
3. Pick **Mode** + **Speed**, press **Play**.
4. **Graph** toggle → chart panel. Tick signals in the left checklist (All / None).

## Graphing

- The series are built **once at log load**: every frame is run through
  `DbcDecoder.DecodeSignals` and grouped per `message·signal` into time-series.
- **A DBC is required** to get numeric signals. **J1939 fallback yields nothing
  plottable** — it only decodes ID transport fields (PGN/SA), no numeric SPNs.
- Static view = whole-log curves with autoscale. While **Replay → grid** plays, the
  chart **sweeps live**: it draws each selected signal only up to the current
  playback time, axes pinned to the full range (a ~15 fps `DispatcherTimer` clips
  the precomputed arrays via binary search). Pause freezes the sweep; Stop shows the
  full static curve. The live sweep is **grid-replay only** — bus replay stays static.

## Using it to validate a signal/calibration

After editing a DBC (see `.claude/skills/decode-can-signal-from-logs`), reload the
DBC, open the same log, tick the signal, and eyeball the curve against the known
event (idle flat at the idle value, revs where you revved, speed 0 while stationary).
A wrong scale shows as a right-shaped-but-wrong-magnitude curve.

## Related
- `README.md` → "Log playback" / "Graph view" notes (format detect, 5000-row cap, bus echo).
- `.claude/skills/decode-can-signal-from-logs` — find/calibrate the signal in the first place.
