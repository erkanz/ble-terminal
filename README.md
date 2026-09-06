# BLE Serial Terminal

Native Windows BLE/GATT serial terminal, GATT diagnostic tool, and KISS / AX.25 / APRS analyzer built with .NET 8 / WPF.

This repository is the **canonical source and build location** for the project.

## Project status

Current hardware-qualified BLE baseline: **v13 RT-950 GATT Cache / FFE1 Notify Fix**.

- **Phase A** — RT-950 hardware qualification: COMPLETE
- **Phase B** — KISS / AX.25 / APRS decoder: CI COMPLETE, final real-hardware decoded-window validation pending
- **Phase C** — BLE Notification Monitor: CI COMPLETE, hardware/functional validation pending
- **Phase D** — Log Filters / Search: CI COMPLETE, runtime validation pending
- **Phase E** — Session Capture / Offline Replay: implemented with automated tests; real RT-950 capture/replay validation pending
- **RT-950 Manual GATT TX diagnostics** — implemented for direct FFE1 vs FF31 host-to-radio testing; real RT-950 TX/response validation pending

Project continuity and test rules are maintained in:

- `ROADMAP.txt`
- `RT950_TEST_CHECKLIST.txt`
- `RT950_MANUAL_GATT_TX_TEST_CHECKLIST.txt`
- `PHASE_B_TEST_CHECKLIST.txt`
- `PHASE_C_TEST_CHECKLIST.txt`
- `PHASE_D_TEST_CHECKLIST.txt`

## BLE / GATT

- Auto Detect BLE-UART
  - Nordic UART Service (NUS)
  - HM-10 / FFE0-FFE1
  - generic UART-style GATT profiles
- Radtel RT-950 Pro FFE0/FFE1 KISS profile support
- connection-scoped cached Auto Detect GATT objects to avoid RT-950 second-FFE0-enumeration / false `AccessDenied` regressions
- result-bearing CCCD writes with status/protocol-error diagnostics
- raw BLE notification logging before protocol parsing
- shared GATT operation serialization between main terminal and GATT Inspector
- GATT Inspector with services, characteristics, descriptors, Read/Write, Notify/Indicate and cache/source/ownership metadata

### RT-950 manual GATT routing

After the selected service has been discovered, the main terminal exposes live routing controls:

- **Service** — current retained service wrapper; for RT-950 this is FFE0
- **Notify characteristic** — notification/indication-capable characteristics from the retained service
- **Write characteristic** — characteristics from the retained service, including FFE1 and FF31 when actually exposed by the radio
- **Write type** — With Response / Without Response

The existing Auto Detect profile and v13 ownership/cache model remain intact. Selecting FF31 as the TX characteristic changes the active TX route only; it does not disable the existing FFE1 notification subscription.

The application logs the actual characteristic property flags reported by Windows for every retained service characteristic. FF31 is **not assumed to be writable**. If the selected write type is unsupported by the real characteristic properties, Send is disabled or the attempted operation is rejected with an explicit diagnostic.

Two routing presets are provided:

- **RT950 OEM TEST** → FFE0 / Notify FFE1 / Write FF31 / With Response / HEX / NONE
- **RT950 FFE1 TEST** → FFE0 / Notify FFE1 / Write FFE1 / With Response / HEX / NONE

Presets change settings only and never transmit automatically.

### Detailed write diagnostics

Each manual write reports the real route and wire bytes before the WinRT GATT result is interpreted. Example:

```text
*** RAW BLE WRITE
*** COMMAND_SLOT=1
*** SERVICE=FFE0
*** UUID=FF31
*** TYPE=WITH_RESPONSE
*** LEN=14
*** HEX=50 52 4F 47 52 41 4D 42 54 39 30 30 30 55
*** WRITE START RESULT=ACCEPTED
```

For a result-bearing write the application reports the real `GattWriteResult` status, numeric status code and ATT protocol error when available. `WRITE START RESULT=ACCEPTED` means that Windows accepted creation of the asynchronous GATT write operation; it does **not** mean the radio transmitted RF or accepted an OEM command at its application layer.

For Without Response, `WRITE SUBMITTED NO_RESPONSE` is logged as a separate diagnostic; any result Windows still provides is also retained.

When FFE1 receives the single byte `06`, the terminal additionally prints:

```text
*** RT950 OEM ACK RECEIVED: 06
```

Raw HEX remains authoritative and visible.

## Multi-command TX workbench

The TX area is a bounded, collapsible/scrollable command workbench so the RX terminal remains the dominant part of the window.

- 1 to 5 independent command rows
- each row has an editable label, independent payload field and independent Send button
- **Settings → Number of TX command rows → 1..5** changes visible rows dynamically
- default visible row count is 1
- labels, contents and row count persist in `%LOCALAPPDATA%\BLESerialTerminal\tx-command-settings.json`
- hidden rows retain their saved contents
- Send never clears the command input
- rapid Send 1 / Send 2 / ... requests are queued and executed sequentially
- each request captures its own payload and route so payloads are not merged
- every runtime write includes `COMMAND_SLOT=N`

**Load RT950 Test Commands** explicitly loads an OEM test set; it never transmits automatically:

```text
Command 1 label: OEM Handshake
Command 1: 50 52 4F 47 52 41 4D 42 54 39 30 30 30 55

Command 2 label: Model Query
Command 2: 4D
```

## TX payload rules

TX modes remain ASCII and HEX. Line endings remain:

- NONE
- CR
- LF
- CRLF

LF remains the normal default. RT-950 test presets explicitly select NONE.

HEX accepts both forms:

```text
01 A0 FF
01A0FF
```

With HEX + NONE, no byte is appended. For the OEM handshake the wire payload is exactly 14 bytes:

```text
50 52 4F 47 52 41 4D 42 54 39 30 30 30 55
```

Invalid HEX is rejected rather than silently altered.

## KISS / AX.25 / APRS

The layered protocol path remains:

```text
RAW BLE notification → KISS stream reassembly → AX.25 → APRS
```

Capabilities include fragmented KISS reassembly, multiple KISS frames per notification, FEND/FESC handling, malformed-escape visibility, AX.25 source/destination/path/control/PID decoding, and APRS position/message/ACK/status/object/item/telemetry/weather/Mic-E/third-party categorization with raw-information retention.

### APRS decode presentation

Higher-layer AX.25/APRS decode summaries are **not injected into the serial output area**. Open:

**View → Decoded KISS / AX.25 / APRS packets...**

The dedicated packet window shows timestamp, BLE characteristic, KISS port/command, AX.25 source/destination/path, type, summary, raw/unescaped KISS HEX, AX.25 fields, original APRS information, decoded APRS fields and warnings. Structured diagnostic logging still records AX.25/APRS events for filters/session capture.

## BLE Notification Monitor

Open **View → BLE Notification Monitor...** for an independent, bounded notification capture path with sequence, timestamp, device, service/characteristic UUID, source/reused metadata, delivery mode, length, HEX, ASCII-safe rendering, KISS frame count, total counters and per-characteristic counters.

Pause affects only display updates; capture continues. The monitor does not create a competing FFE1 subscription for the main terminal path.

## Diagnostic Log Filters / Search

Open **View → Diagnostic Log Filters / Search...**.

The bounded structured log supports `CONNECTION`, `DISCOVERY`, `GATT`, `CCCD`, `RX_RAW`, `TX_RAW`, `KISS`, `AX25`, `APRS`, `INSPECTOR`, `WARNING` and `ERROR`, with text/category/device/characteristic/direction filters, errors/warnings-only mode, previous/next navigation, full/filtered export and session metadata.

Raw RX/TX entries carry binary payload bytes in addition to formatted text so later session capture/replay does not depend only on rendered terminal strings.

## Session Capture / Offline Replay

Open **View → Session Capture / Offline Replay...**.

The application can record a versioned `.blsession.json` session containing structured events, binary RX/TX data, device/profile metadata and relevant GATT mapping. Offline replay feeds captured RX_RAW events back through independent per-characteristic KISS state and the normal AX.25/APRS decoders. Controls include Play, Pause, Step, Reset and replay speed.

Replay never transmits captured TX bytes to a live BLE device.

## Terminal / UI

- full dark mode and dark scrollbars
- configurable serial output foreground/background colors
- File / View / Settings / About menus
- Export Log
- LF default line ending
- Local echo default off
- Enter-to-send on each command row
- transparent multi-resolution application icon
- self-contained single-file Windows x64 publish

## Canonical Windows build

GitHub Actions is the authoritative build path. Every push to `main`, pull request and manual workflow run executes:

- static regression checks
- protocol decoder tests
- notification capture tests
- log filter tests
- session capture/replay tests
- manual RT-950 GATT TX payload/queue tests
- .NET restore
- Windows x64 self-contained single-file publish
- SHA-256 generation
- artifact upload

Open **Actions → Build Windows EXE**.

Expected artifact contents:

```text
BLESerialTerminal.exe
BLESerialTerminal.exe.sha256
```

The EXE is Windows x64, self-contained and requires no separate .NET runtime installation.

## RT-950 manual FF31 vs FFE1 hardware test

Follow `RT950_MANUAL_GATT_TX_TEST_CHECKLIST.txt`.

Core comparison:

1. connect RT-950 with Auto Detect and wait for `RADTEL KISS READY`
2. confirm FFE1 Notify is active and inspect the real FF31 property flags
3. click **Load RT950 Test Commands**
4. click **RT950 OEM TEST** and Send 1
5. capture the FF31 write status and any FFE1 response / OEM `06`
6. click **RT950 FFE1 TEST** without changing the 14-byte payload
7. Send 1 again and compare the FFE1 write status/response
8. receive another ordinary KISS/APRS packet afterward to prove FFE1 RX was not broken by the manual TX route

The code/CI gate cannot substitute for this radio-side test. Do not mark FF31/FEE1 host-to-radio behavior hardware-qualified until those runtime logs are captured from the real RT-950.

## Other phase validation

- Phase B: `PHASE_B_TEST_CHECKLIST.txt`
- Phase C: `PHASE_C_TEST_CHECKLIST.txt`
- Phase D: `PHASE_D_TEST_CHECKLIST.txt`
- RT-950 v13 baseline: `RT950_TEST_CHECKLIST.txt`

## Local Windows publish

With .NET 8 SDK installed:

```bat
PUBLISH_SINGLE_EXE_WIN64.bat
```

## Ubuntu cross-build

Historical Ubuntu cross-publish helpers remain for compatibility/debug use, but GitHub Actions on `windows-latest` is the authoritative release environment.

## Runtime requirements

- Windows 10 2004+ or Windows 11 x64
- Bluetooth Low Energy adapter
