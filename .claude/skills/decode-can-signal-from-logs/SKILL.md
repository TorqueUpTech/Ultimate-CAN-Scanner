---
name: decode-can-signal-from-logs
description: >
  Reverse-engineer and calibrate CAN signals (RPM, speed, pedal, temp, ...) from
  captured CSV trace logs, then write them into a DBC. Use when the user says a
  signal is "actually" something else, wants to find which CAN ID/byte carries a
  value, wants to calibrate a scale/offset against known readings, or asks to
  verify a DBC mapping against logs. Covers the CSV format, awk decode recipes,
  the stationary-vs-moving discriminators, and how to cross-validate across logs
  before editing the DBC.
---

# Decoding & calibrating a CAN signal from logs

The reliable way to map a value to a CAN ID/byte is **not** to guess from a single
log — it is to capture a few logs that *decouple* the candidate signals, then prove
the mapping with a discriminator and calibrate against a known reading. A signal
that "looks like RPM" in one log is often pedal, throttle, or load; correlated
signals are indistinguishable until you find a log where they move independently.

> Hard-won example (Nissan Patrol Y61): the field labelled `VehicleSpeed` reads 0
> in every stationary rev test, the field labelled `EngineRPM` floors to 100% on
> the dyno (it's the **pedal**), and the **real RPM** was on an undecoded field
> (`0x23D` bytes 3-4) nobody had looked at. Only multi-log cross-validation found it.

## CSV trace format (what CAN-Tool writes / LogFile.cs reads)

```
Time(s),Dir,ID,Type,DLC,Data
0.00355,RX,233,Std,8,000E110000000000
```
- **ID** is hex *without* `0x`. 29-bit (extended) IDs carry a trailing `x` (`18FEF100x`).
- **Data** is contiguous hex, 2 chars/byte, in the `Convert.ToHexString` order.
- Legacy GM logs (≥7 cols, `HH:MM:SS` in col 2, space-separated hex) are also accepted.

## Byte-extraction recipes (Git Bash `awk`)

`$3` = ID, `$6` = Data. Byte *N* (0-based) = `substr($6, 1+N*2, 2)`.

```bash
# 1. What IDs are present, by frequency
tail -n +2 log.csv | awk -F',' '{print $3}' | sort | uniq -c | sort -rn

# 2. One byte of one ID over time:  byteN = strtonum("0x" substr($6,1+N*2,2))
tail -n +2 log.csv | awk -F',' '$3=="2D1"{print $1, strtonum("0x" substr($6,3,2))}'   # byte1

# 3. 16-bit little-endian @ bytes a..a+1:  lo + hi*256   (big-endian: hi + lo*256)
tail -n +2 log.csv | awk -F',' '$3=="233"{lo=strtonum("0x" substr($6,3,2)); \
  hi=strtonum("0x" substr($6,5,2)); print $1, lo+hi*256}'                              # bytes1-2 LE

# 4. Which bytes of an ID actually move? (constant bytes are flags/counters/padding)
tail -n +2 log.csv | awk -F',' '$3=="2D1"{for(i=0;i<length($6)/2;i++){ \
  b=strtonum("0x" substr($6,1+i*2,2)); if(b<mn[i]||!(i in mn))mn[i]=b; if(b>mx[i])mx[i]=b}} \
  END{for(i in mn) printf "byte%d: %d..%d\n",i,mn[i],mx[i]}'
```

## Discriminators — design the captures so signals decouple

Take **several short logs**, each pinning one variable so the others separate:

| Capture | What it isolates |
|---|---|
| **Idle, stationary** (note the idle RPM) | Calibration anchor; baseline for every field |
| **Rev in neutral, stationary** | RPM/pedal move, **speed stays 0** → speed falls out |
| **Engine OFF, pump pedal** | Pedal moves, **RPM stays 0** → pedal vs RPM separate |
| **Stepped holds** (idle→1500→2500) | RPM shows 3 plateaus → identify + calibrate the rpm field |
| **Driving / dyno** | Speed non-zero; pedal floors to 100% under load |

Decision rules that actually settle it:
- **Stays 0 while revving stationary ⇒ it's road speed**, not engine/throttle.
- **Floors (full range) under load but only partial when free-revving ⇒ pedal/throttle**, not RPM (pedal is *not* proportional to RPM).
- **Lead/lag:** the driver input (pedal) *leads*; the response (RPM) *lags* ~100-150 ms.
- **Proportionality:** a true tach field is ~proportional to RPM. Check the ratio of
  window-means at known holds (700:1500:2500 ≈ 1 : 2.1 : 3.6). Pedal-shaped fields
  give a flatter ratio (~1 : 1.8 : 2.7).

## Find an unknown field (don't assume it's on a labelled ID)

Scan **every ID, every byte / 16-bit pair** for the profile you expect. Example:
find a field whose means at the idle / 1500 / 2500 windows are RPM-proportional:

```bash
tail -n +2 idle-1500-2500.csv | awk -F',' '
function w(t){if(t>=0.3&&t<=1.3)return 1; if(t>=9.5&&t<=14)return 2; if(t>=22&&t<=30)return 3; return 0}
{win=w($1+0); if(!win)next; d=$6;
 for(i=0;i<length(d)/2-1;i++){lo=strtonum("0x" substr(d,1+i*2,2)); hi=strtonum("0x" substr(d,1+(i+1)*2,2));
   k=$3"|"i"|LE"; s[k,win]+=lo+hi*256; c[k,win]++}}
END{for(k in c){split(k,a,SUBSEP); if(a[2]!=1)continue; K=a[1];
  m1=s[K,1]/c[K,1]; m2=s[K,2]/c[K,2]; m3=s[K,3]/c[K,3];
  if(m1>20&&m2>m1&&m3>m2&&m2/m1>1.8&&m2/m1<2.5&&m3/m1>3.0&&m3/m1<4.2)
    printf "%-12s idle=%.0f 1500=%.0f 2500=%.0f\n",K,m1,m2,m3}}'
```
Tune the window times and the ratio gates to the signal you're chasing.

## Calibrate (scale, offset)

Two known (raw, real) points give a line: `slope = Δreal/Δraw`, `offset = real - slope*raw`.
Use the most stable holds (lowest per-window standard deviation), e.g.
`(240,700)` and `(758,2500)` → `slope 3.48, offset -135` → `RPM = 3.48*raw - 135`.
Then **back-check the line on a different log** — if a known-sane log produces an
absurd value (e.g. 18000 rpm on a cruise log), the field isn't what you think.

## Cross-validate before editing the DBC

Run the candidate formula across **all** logs and confirm each matches reality
(idle≈known idle, off≈0, driving≈sane). Only then edit the DBC. In a `.dbc`:
```
 SG_ EngineSpeed : 24|16@1+ (3.48,-135) [0|8000] "rpm" Vector__XXX
```
`24|16@1+` = start bit 24 (byte 3), 16 bits, `@1`=little-endian, `+`=unsigned;
`(scale,offset)`, `[min|max]`, `"unit"`. Record *how* you calibrated it (which logs,
which holds) in a `CM_ SG_ <id> <name> "..."` comment — future-you needs the why.

## If the contradiction is with the user

When the logs disprove how a signal was described, **surface it with the evidence
table** (signal × capture) and recommend the data-driven mapping — don't silently
overwrite a stated mapping, and don't blindly apply a scale that the field's width
or range can't support.

## Related
- `src/Logging/LogFile.cs` — the reader these recipes mirror (format auto-detect).
- `src/Decoding/DbcDecoder.cs` — `DecodeSignals` turns a frame into numeric values.
- `.claude/skills/replay-and-graph-logs` — view/calibrate a candidate visually.
