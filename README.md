# Ultimate CAN Scanner

A Windows WPF (.NET 8) CAN-bus tool with Ixxat, OBDX and J2534 backends. Three adapter
backends are selectable at runtime
(**Adapter** picker on the connection bar): the **Ixxat VCI4 .NET API** for HMS Ixxat
USB-to-CAN V2 adapters, the **OBDX Pro** scantool over its DVI protocol, and any
**SAE J2534** PassThru device via its installed vendor DLL.

## Features

- **Pluggable adapters** behind one `ICanAdapter` interface: Ixxat VCI4, OBDX Pro
  over USB virtual-COM / WiFi TCP / BLE, or a generic **J2534** PassThru tool. Pick the
  backend, then the device.
- Enumerate and connect to any installed VCI device (USB-to-CAN V2, etc.)
- Selectable bit rate (10 kbit … 1 Mbit) and listen-only mode
- Live receive trace with hardware timestamps (RX + self-received TX), with an **auto-scroll** toggle
- **RX ID filter**: trim the trace grid to just the CAN IDs you care about — an allowlist
  (show only listed IDs) or blocklist (**Exclude**). Accepts space/comma-separated hex IDs
  with an optional `0x` prefix and inclusive ranges, e.g. `100 7E8 700-7FF`. Display-only:
  logging and the live gauges still see every frame
- Transmit standard (11-bit) or extended (29-bit) data/RTR frames
- **Auto-transmit / repeat** of a raw frame or a DBC signal at a configurable interval (ms)
- On-the-fly decode: **DBC** database (loaded at runtime) with **J1939** as a fallback
- **DBC transmit**: pick a message → signal → physical value; the value is scaled/packed per the DBC and sent
- **Multi-signal TX list**: queue several signals (**Add to list**), then **Send All** or **Repeat All**.
  Signals sharing a CAN ID are packed into one frame; different IDs send as separate frames and, when
  repeating, as separate cyclic streams. Values are editable inline in the list
- CSV trace logging
- **Log playback**: open a captured CSV trace and either *view* all frames at once, *replay* them
  into the grid at recorded timing (offline), or *replay onto the CAN bus* at recorded timing —
  with a speed multiplier and Play/Pause/Stop (ported from the Python DBC-Tool's log viewer)
- **Graph view**: a **Graph** toggle plots decoded signals from the loaded log on a time axis
  (ScottPlot, pan/zoom/autoscale). Pick signals from the checklist; needs a DBC loaded to turn
  frames into numeric signals
- **Live Gauges**: a **Live Gauges** tab turns decoded signals into human-friendly gauge cards
  (big value + unit + a bar scaled to the DBC min/max) that update in real time from the live
  bus *or* from playback. Tick each CAN ID to watch its signals; needs a DBC loaded

## Requirements

- Windows x64
- **Ixxat VCI driver** installed (`C:\Program Files\HMS\Ixxat VCI`) — already present on this machine
- **.NET 8 SDK** (only the runtime is currently installed — see Setup)

## Setup

```powershell
# 1. Install the .NET 8 SDK (one-time)
winget install --id Microsoft.DotNet.SDK.8 -e

# 2. Restore the Ixxat.Vci4 NuGet package and build
dotnet build src\IxxatCanTool.csproj -c Release

# 3. Run
dotnet run --project src\IxxatCanTool.csproj -c Release
```

## Project layout

| Path | Purpose |
|------|---------|
| `src/Can/ICanAdapter.cs` | The adapter contract (connect / send / cyclic / RX+error events) the UI targets. |
| `src/Can/CanAdapters.cs` | Enumerate + construct the concrete backend for a `CanAdapterKind`. |
| `src/Can/CanBusService.cs` | Ixxat VCI4 backend: enumerate, connect, RX thread, TX. All vendor types confined here. |
| `src/Can/Obdx/ObdxDvi.cs` | OBDX Pro DVI byte protocol: checksum, command builders, streaming frame parser (pure, tested). |
| `src/Can/Obdx/ObdxCanAdapter.cs` | OBDX backend: ELM→DVI handshake, raw-CAN init, RX pump, TX, software-timer cyclic. |
| `src/Can/Obdx/ObdxTransports.cs` | OBDX byte pipes: USB virtual-COM (`SerialPort`) and WiFi (`TcpClient`). |
| `src/Can/J2534/J2534Native.cs` | SAE J2534 v04.04 constants, `PASSTHRU_MSG` layout, and the vendor-DLL loader (`J2534Library`). |
| `src/Can/J2534/J2534Registry.cs` | Enumerate installed J2534 drivers from `HKLM\SOFTWARE\PassThruSupport.04.04` (64- and 32-bit views). |
| `src/Can/J2534/J2534CanAdapter.cs` | J2534 backend: load DLL, raw-CAN connect, pass-all filters, RX pump, TX, software-timer cyclic. |
| `src/Can/CanDeviceInfo.cs` | Adapter-agnostic device descriptor (`Adapter` + opaque `Key`). |
| `src/Can/CanFrame.cs` | Driver-agnostic frame model. |
| `src/Can/CanIdFilter.cs` | Immutable RX trace filter: parse hex IDs/ranges + include/exclude membership test. |
| `src/Decoding/J1939Decoder.cs` | 29-bit ID → J1939 fields + known-PGN table. |
| `src/Decoding/DbcDecoder.cs` | Loads a DBC, decodes frames to signals, exposes messages for TX. |
| `src/Decoding/DbcMessageInfo.cs` | UI-facing DBC message/signal wrappers + TX signal packing. |
| `src/Logging/TraceLogger.cs` | Thread-safe CSV trace writer. |
| `src/Logging/LogFile.cs` | Reads a captured CSV trace back into `CanFrame`s (can-trace, IXXAT canAnalyser3 export + legacy GM formats) for playback. |
| `src/ViewModels/MainViewModel.cs` | UI state, commands, frame buffering, playback + graph series. |
| `src/ViewModels/PlotSignal.cs` | One plottable signal time-series (times/values + selection flag). |
| `src/ViewModels/LiveSignal.cs` | One live gauge: latest value + DBC/observed range for the bar. |
| `src/ViewModels/LiveMessageGroup.cs` | A CAN ID (DBC message) that can be enabled to show its gauges. |
| `src/ViewModels/TxSignalEntry.cs` | One queued signal (message + signal + editable value) in the multi-signal TX list. |
| `src/MainWindow.xaml` | WPF UI (Trace tab + Live Gauges tab). |

## Notes / next steps

- **Adapters** are chosen at runtime via the connection-bar picker. `CanBusService`
  (Ixxat VCI4), `ObdxCanAdapter` (OBDX Pro) and `J2534CanAdapter` (generic J2534) all
  implement `ICanAdapter`; the view model holds one `ICanAdapter` and swaps it
  (`EnsureBusKind`) when the picker changes. `CanDeviceInfo` carries an opaque `Key` per
  backend (VCI object id, `serial:COM5` / `tcp:host:port`, or the J2534 driver DLL path).
- **J2534 backend** (`src/Can/J2534/`) drives any SAE **J2534-1 (v04.04)** PassThru tool.
  Drivers are discovered from the registry (`HKLM\SOFTWARE\PassThruSupport.04.04`) — each
  device's `Name` and `FunctionLibrary` DLL path — and the chosen DLL is loaded by path at
  runtime (`NativeLibrary` + delegate P/Invoke of `PassThruOpen/Connect/ReadMsgs/WriteMsgs/
  StartMsgFilter/…`), so one build supports every installed tool with no per-vendor NuGet.
  On connect it opens a raw **CAN** channel (protocol 5) at the selected bit rate and
  installs pass-all filters for **both 11- and 29-bit** IDs (J2534 blocks all RX until a
  filter is set). RX runs on a background thread that reads a batch of `PASSTHRU_MSG`s from
  one reused unmanaged buffer and pulls each frame's fields by offset (no full 4152-byte
  struct copy per frame); the CAN ID is the first 4 payload bytes, big-endian. TX is
  mirrored into the trace and cyclic TX uses a software timer over the `Send` path (so
  repeats echo into the grid), matching OBDX — `SupportsScheduler = false`.
  **Caveats:** this process is **x64**, so only 64-bit vendor drivers load — 32-bit-only
  drivers are still *listed* (so you see the device) but marked unusable and raise a clear
  error on connect. SAE J2534-1 has **no standard listen-only** CAN mode, so ticking
  Listen-only connects in normal mode and reports that in the status bar (the tool will ACK).
- **OBDX Pro backend** speaks the DVI byte protocol (`ObdxDvi`, verified against the
  manual's worked hex). On connect it does the ELM→DVI handshake (`ATE0`, `DXDP1`), then
  puts the tool in **raw** mode for reverse-engineering: monitor-all (clear filters),
  **auto ISO-TP reassembly OFF** and **auto write-formatting OFF** — otherwise multi-frame
  traffic is merged and TX gets an injected length byte. Timestamps are taken on arrival;
  cyclic TX uses a software timer (the OBDX's 8 hardware periodic-frame slots are a future
  optimisation, so it reports `SupportsScheduler = false`).
- **OBDX transports** (`ObdxTransports.cs` + `BleObdxTransport.cs`): **USB** virtual COM,
  **WiFi** TCP (SoftAP `192.168.4.1:23`), and **BLE** via the Nordic UART Service it
  advertises (service `6E400001-…`, write `…0002`, notify `…0003`; no pairing). BLE uses
  the WinRT Bluetooth APIs, which is why the app targets `net8.0-windows10.0.19041.0`.
  Caveat: `Connect` is synchronous, so a BLE scan/connect briefly blocks the UI (up to a
  few seconds) — making connect async is a future improvement.
- The app pins itself to **x64** to match the 64-bit VCI driver DLLs.
- The J1939 PGN table is intentionally small; extend `KnownPgns` as needed.
- **DBC transmit** (single signal) sends the chosen signal in a payload sized to the
  message DLC; all *other* signals in that message are left at zero. To populate several
  signals of a frame (or drive several CAN IDs at once), use the **TX list**: queued
  entries are grouped by CAN ID and each group's signals are packed into one frame via
  `DbcMessageInfo.EncodeSignals` (later entries win on overlapping bits). **Repeat All**
  opens one cyclic stream per distinct ID — the bus layer already keyed cyclic messages by
  handle (`_cyclic`/`_softCyclic`), so multiple simultaneous streams need no driver change;
  only the VM had serialized them. **Stop** (any Repeat button) tears down every stream.
- **Repeat / auto-transmit** uses the adapter's hardware cyclic scheduler
  (`ICanScheduler`), so cycle timing is realised on the device, not by a software
  timer — no jitter. The ms interval is converted to scheduler ticks from the
  socket's `ClockFrequency` / `CyclicMessageTimerDivisor`. One stream runs at a
  time (raw *or* DBC signal); starting one stops the other. If an adapter reports
  no scheduler support, Repeat reports it in the status bar.
- **RX ID filter** (`src/Can/CanIdFilter.cs`) trims what the trace grid shows without
  touching the RX pipeline: it is applied at the single point where frames are buffered for
  display (live RX *and* log playback), so CSV logging and the live gauges still receive every
  frame. The filter text compiles to an immutable `CanIdFilter` (a set of inclusive `[lo,hi]`
  ID ranges plus include/exclude mode) that is swapped into a `volatile` field on the UI thread
  and read lock-free on the RX/playback thread. IDs are masked to 29 bits, so driver flag bits
  never affect matching; malformed tokens are skipped so live typing never throws.
- The trace grid is fed by a **50 ms batched flush** (`ConcurrentQueue` drained by
  a `DispatcherTimer`) instead of one UI update per frame. This keeps buttons
  responsive under heavy bus load (previously a fast bus posted `Normal`-priority
  updates that outranked and starved `Input`, freezing the UI).
- **Log playback** auto-detects the file layout: the `Time(s),Dir,ID,Type,DLC,Data`
  header (what this tool writes) selects the can-trace parser — 29-bit IDs carry a
  trailing `x` and data is contiguous hex; the quoted IXXAT **canAnalyser3** trace
  export (`"Bus","No","Time (abs)","State","ID (hex)","DLC","Data (hex)","ASCII"`)
  selects the canAnalyser parser — fields are double-quoted and the "No" counter
  carries a thousands-separator comma, both handled by the quote-aware CSV split;
  anything else falls back to the legacy GM format (`HH:MM:SS` timestamp +
  space-separated hex). Pacing uses inter-frame
  timestamp deltas divided by the speed multiplier; `Task.Delay` resolution (~15 ms
  on Windows) means very tight traces play back slightly coarser than recorded. The
  **Loop** toggle repeats a paced run from the first frame until Stop (loops run
  back-to-back; re-read each pass so toggling mid-run ends after the current pass;
  *View* ignores it).
  *View* mode dumps every decoded frame into the grid (which keeps the most recent
  5 000 rows like the live trace). *Replay → bus* re-transmits via the normal `Send`
  path, so frames echo back into the grid via self-reception and are logged if a
  trace log is running. *Replay → TCP* broadcasts each frame over TCP in the 13-byte
  RawCanWire format (`src/Tcp/`) instead of onto the bus — feeding the Can-Display
  dash sim with no adapter (same wire format and `127.0.0.1:51729` port as the
  CAN-Replay tool).
- **TCP broadcast server** (`src/Tcp/TcpFrameServer.cs`): the **TCP Server** toggle in
  the playback bar starts/stops a loopback listener on `127.0.0.1:51729`; the status
  text shows the attached client count. Choosing *Replay → TCP* and pressing Play
  auto-starts it if needed and leaves it up across passes, so a connected dash sim
  isn't dropped between plays (only the **TCP Server** toggle or app exit stops it).
  The byte layout: byte 0 = DLC | `0x80` extended flag, bytes 1-4 = 32-bit big-endian
  ID, bytes 5-12 = zero-padded data.
- **Graph view** builds its series once at log load: every frame is DBC-decoded to numeric
  signal values (`DbcDecoder.DecodeSignals`) and grouped per `message·signal` into time-series.
  It needs a **DBC loaded** before opening the log; J1939 only yields ID transport fields
  (PGN/SA), no numeric SPNs, so it contributes no plottable signals. Rendering uses
  **ScottPlot.WPF**.
- The chart has two behaviours: while **Replay → grid** is playing it sweeps **live**, drawing
  each selected signal only up to the current playback time over axes pinned to the full log
  range (a `DispatcherTimer` at ~15 fps clips the precomputed arrays via a binary search —
  `MainWindow.CountUpTo`). Pausing freezes the sweep; stopping (or any other mode) shows the
  **whole static curve** with autoscale, like DBC-Tool. The live sweep is intentionally scoped
  to grid replay; bus replay still shows the static chart.
- CAN-FD is a natural follow-up (the VCI API exposes `ICanControl2` /
  `ICanChannel2`, and DbcParserLib carries 64-byte payloads).
