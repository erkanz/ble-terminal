# BLE Serial Terminal

Native Windows BLE/GATT serial terminal, GATT diagnostic tool, and KISS / AX.25 / APRS analyzer built with .NET 8 / WPF.

This repository is the **canonical source and build location** for the project.

## Project status

Current baseline: **v13 RT-950 GATT Cache / FFE1 Notify Fix**

Current release gate: **Phase B/C/D real-hardware / functional validation**

**Phase A is complete.** The RT-950 FFE0/FFE1 cache, CCCD, raw notification, fragmented KISS reassembly, and GATT Inspector lifecycle baseline was qualified on real hardware.

**Phase B — KISS / AX.25 / APRS Decoder** is implemented and CI-green. The remaining gate is a real RT-950 end-to-end packet check in the decoded-packet window.

**Phase C — Notification Monitor** is implemented and CI-green. It remains hardware/functional-test pending until exercised on the RT-950.

**Phase D — Log Filters / Search** is implemented and CI-green. It remains runtime functional-test pending before it can be marked complete.

Project continuity and phase rules are maintained in:

- `ROADMAP.txt`
- `RT950_TEST_CHECKLIST.txt`
- `PHASE_B_TEST_CHECKLIST.txt`
- `PHASE_C_TEST_CHECKLIST.txt`
- `PHASE_D_TEST_CHECKLIST.txt`

## Current capabilities

### BLE / GATT

- Auto Detect BLE-UART
  - Nordic UART Service (NUS)
  - HM-10 / FFE0-FFE1
  - generic UART-style GATT profiles
- Radtel RT-950 Pro FFE0/FFE1 KISS profile support
- Cached Auto Detect GATT objects to avoid RT-950 FFE0 re-enumeration / false `AccessDenied` negatives
- Real CCCD Notify subscription result logging
- Raw BLE notification logging before protocol parsing
- GATT Inspector
  - all services / characteristics / descriptors
  - characteristic properties
  - manual Read / Write
  - multi-characteristic Notify / Indicate
  - source/reuse metadata for cached Auto Detect objects
- shared GATT operation serialization between main terminal and Inspector

### KISS / AX.25 / APRS

- stream-safe KISS reassembly across fragmented BLE notifications
- multiple KISS frames from one notification
- FEND/FESC/TFEND/TFESC handling
- malformed KISS escapes are flagged without hiding original bytes
- structured KISS port/command decoding
- AX.25 decoding:
  - destination callsign / SSID
  - source callsign / SSID
  - digipeater path
  - repeated/H bit for path entries
  - control field
  - PID
  - frame type
  - information field
- KISS AX.25 frames are **not rejected for missing HDLC FCS**; ordinary KISS TNC delivery does not require the FCS to be present
- APRS categorization / parsing for:
  - position
  - timestamped position
  - message
  - ACK / REJ
  - status
  - object
  - item
  - telemetry
  - weather / weather extensions
  - Mic-E identification with raw payload retention
  - third-party encapsulation
  - query / capabilities / user-defined
  - unknown/unsupported types with raw information retained

Open:

**View → Decoded KISS / AX.25 / APRS packets...**

The packet viewer shows timestamp, BLE characteristic, KISS port/command, AX.25 source/destination/path, frame/APRS type, summary, raw/unescaped KISS HEX, AX.25 fields, original APRS information, decoded fields, and warnings. Packet details can be copied or exported. History is bounded.

### BLE Notification Monitor

Open:

**View → BLE Notification Monitor...**

The monitor is a diagnostic capture path independent from the scrolling terminal view. It records BLE notification/indication events with sequence, timestamp, device, service/characteristic UUID, source/reused metadata, delivery mode, byte length, HEX, ASCII-safe rendering, KISS-frame count where applicable, and total/per-characteristic counters.

Monitor behavior:

- **Pause display** stops UI updates only; capture continues in the bounded background store
- Resume rebuilds the visible list from captured history
- Clear resets monitor capture history/counters only
- Copy HEX / Copy text
- TSV export
- DataGrid virtualization and bounded visible history
- main terminal FFE1 capture uses an independent ValueChanged observer and independent KISS decoder
- Inspector multi-characteristic subscriptions can also feed the monitor without creating an extra CCCD subscription
- Auto Detect FFE1 reused by Inspector is not intentionally double-counted
- monitor exceptions are isolated from terminal/Inspector RX processing

### Diagnostic Log Filters / Search

Open:

**View → Diagnostic Log Filters / Search...**

Phase D adds a structured, bounded diagnostic event log with these categories:

- `CONNECTION`
- `DISCOVERY`
- `GATT`
- `CCCD`
- `RX_RAW`
- `TX_RAW`
- `KISS`
- `AX25`
- `APRS`
- `INSPECTOR`
- `WARNING`
- `ERROR`

The log window supports:

- free-text search
- category filter
- device filter
- characteristic UUID filter
- RX / TX direction filter
- errors/warnings-only filter
- Previous match / Next match navigation
- automatic scroll suppression while navigating matches
- selected-entry detail view
- copy row
- full export
- filtered export
- session metadata in exports
- UTF-8 text handling
- bounded 50,000-entry structured store
- virtualized/bounded visible result list for sustained traffic

`RX_RAW` entries are captured from the notification capture path rather than scraped from rendered terminal text, preventing duplicate RT-950 raw notification records. Successful `TX_RAW` entries are recorded independently of Local Echo.

### Terminal / UI

- Full dark mode and dark scrollbars
- Configurable serial output foreground/background colors
- File / View / About menus and Export Log
- LF default line ending
- Local echo default off
- Enter-to-send
- Transparent multi-resolution application icon
- Self-contained single-file Windows x64 publish

## Canonical Windows build

GitHub Actions is the primary build path. Every push to `main`, pull request, and manual workflow run executes static regression checks, Phase B protocol tests, Phase C notification-capture tests, Phase D log-filter tests, and builds the Windows x64 application on a real Windows runner.

Open:

**Actions → Build Windows EXE**

The workflow produces:

```text
BLESerialTerminal.exe
BLESerialTerminal.exe.sha256
```

The EXE is Windows x64, self-contained, single-file, and requires no separate .NET runtime installation on the target PC. Tagged versions (`v*`) automatically attach the EXE and checksum to a GitHub Release.

## Latest Phase D CI baseline

Implementation baseline:

```text
Commit: 84c00e33b6f2e0b5d633ff7eb9d4cad7c0a8a262
Actions run: 34016543683
Artifact ID: 9984076272
EXE size: 78,003,385 bytes
EXE SHA-256: ce89dbeb725ecd176696ad179a9f4f0dc46d35ddb6084cafaccd01a2b7eb9be9
```

Automated checks:

- static regression checks: PASS
- protocol decoder tests: 40 PASS
- notification capture tests: 15 PASS
- log filter tests: 14 PASS
- Windows restore: PASS
- self-contained single-file publish: PASS
- artifact upload: PASS

## Phase B validation

Use the newest successful `main` Actions artifact and follow `PHASE_B_TEST_CHECKLIST.txt`.

For the real-hardware completion test:

1. connect the RT-950 with Auto Detect
2. wait for `RADTEL KISS READY`
3. open **View → Decoded KISS / AX.25 / APRS packets...**
4. receive a real APRS packet
5. verify the terminal shows, in order:

```text
RAW BLE NOTIFICATION
KISS RX
AX25 RX SRC=... DST=... PATH=... TYPE=UI PID=F0
APRS RX TYPE=... SUMMARY=...
```

6. verify the decoded-packet row shows source, destination, path, type/summary and preserves the original KISS/APRS data

## Phase C validation

Follow `PHASE_C_TEST_CHECKLIST.txt` using the same newest successful artifact. At minimum, verify FFE1 notifications appear in **View → BLE Notification Monitor...**, Pause display does not stop capture counters, Resume shows captured backlog, and opening/closing GATT Inspector does not duplicate or break the main FFE1 RX path.

## Phase D validation

Follow `PHASE_D_TEST_CHECKLIST.txt`. Validate live filtering/search during real RT-950 traffic, no duplicate `RX_RAW` entry for one FFE1 notification, successful `TX_RAW` logging with Local Echo off, search navigation behavior, full/filtered export, Unicode, disconnect/reconnect, and sustained RX while filters are active.

## Local Windows publish

With .NET 8 SDK installed:

```bat
PUBLISH_SINGLE_EXE_WIN64.bat
```

## Ubuntu cross-build

The historical Ubuntu cross-publish helper remains in the repository for compatibility, but GitHub Actions on `windows-latest` is the authoritative release build environment.

## Runtime requirements

- Windows 10 2004+ or Windows 11 x64
- Bluetooth Low Energy adapter

## RT-950 Pro qualified baseline

Expected Auto Detect profile:

```text
profile=RADTEL_RT950_KISS
service=FFE0
write=FFE1
notify=FFE1
sameCharacteristic=true
```

Expected data-channel sequence:

```text
FFE1 FOUND
FFE1 VALUECHANGED HANDLER ATTACHED
FFE1 CCCD WRITE RESULT=Success
FFE1 NOTIFY ACTIVE
RADTEL KISS READY
RAW BLE NOTIFICATION
```

See `RT950_TEST_CHECKLIST.txt`, `PHASE_A_RT950_SESSION_2026-09-06.txt`, `PHASE_B_TEST_CHECKLIST.txt`, `PHASE_C_TEST_CHECKLIST.txt`, `PHASE_D_TEST_CHECKLIST.txt`, and `V13_RT950_GATT_CACHE_FIX.txt` for current diagnostics and regression expectations.
