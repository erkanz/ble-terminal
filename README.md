# BLE Serial Terminal

Native Windows BLE/GATT serial terminal and diagnostic suite built with .NET 8 / WPF. The repository is the canonical source and GitHub Actions on Windows is the authoritative build path.

## Project status

Hardware-qualified BLE baseline: **v13 RT-950 GATT Cache / FFE1 Notify Fix**.

- **Phase A** — RT-950 hardware qualification: COMPLETE
- **Phase B** — KISS / AX.25 / APRS decoder: CI COMPLETE, real-hardware decoded-window validation pending
- **Phase C** — Notification Monitor: CI COMPLETE, hardware validation pending
- **Phase D** — Log Filters / Search: CI COMPLETE, runtime validation pending
- **Phase E** — Session Capture / Offline Replay: CI COMPLETE, real RT-950 replay validation pending
- **Phase F** — GATT Snapshot / Compare: CI COMPLETE, real snapshot validation pending
- **Phase G** — KISS TCP Bridge: CI COMPLETE on `main`, real RT-950/external-client validation pending
- **Phase H** — Multi-Device Live Compare: CI COMPLETE on `main`, real two-device isolation test pending
- **RT-950 Manual GATT TX diagnostics** — CI COMPLETE, real FF31/FFE1 radio-side validation pending
- **Phase I** — Production hardening / release: next software phase

See `ROADMAP.txt` for the exact handoff state, build evidence and non-negotiable ownership rules.

## BLE / GATT

Supported Auto Detect profiles include:

- Nordic UART Service (NUS)
- HM-10 / FFE0-FFE1
- generic BLE-UART-style Write + Notify/Indicate pairs
- Radtel RT-950 Pro FFE0 / shared FFE1 KISS profile

The RT-950 path keeps connection-scoped GATT wrappers, attaches `ValueChanged` before enabling CCCD, uses result-bearing CCCD writes, and retains the selected service/characteristic list so the GATT Inspector can reuse the qualified live objects instead of performing a second FFE0 enumeration.

Main terminal and Inspector GATT operations are serialized through the shared main GATT gate. Reconnects create fresh wrappers; stale connection-scoped objects must not be reused.

## RT-950 manual GATT TX diagnostics

The main terminal can temporarily route manual diagnostic TX to characteristics exposed by the retained service, including FFE1 and FF31 when the radio actually reports them.

- **RT950 OEM TEST** → FFE0 / Notify FFE1 / Write FF31 / With Response / HEX / NONE
- **RT950 FFE1 TEST** → FFE0 / Notify FFE1 / Write FFE1 / With Response / HEX / NONE

Presets only change settings; they never auto-send. Actual characteristic properties control whether the selected write type is permitted.

The TX workbench provides 1-5 persistent command rows, serialized writes, exact HEX + NONE payload handling, route/payload freezing per queued request, and detailed result-bearing GATT diagnostics. A successful GATT write proves host-to-radio BLE delivery only; it does not prove OEM-command acceptance or RF transmission.

Checklist: `RT950_MANUAL_GATT_TX_TEST_CHECKLIST.txt`

## KISS / AX.25 / APRS

The layered decoder remains additive:

```text
RAW BLE notification -> KISS stream reassembly -> AX.25 -> APRS
```

Capabilities include fragmented KISS reassembly, repeated FEND handling, FESC/TFEND/TFESC, malformed-escape visibility, AX.25 source/destination/path/control/PID decoding and APRS position/message/ACK/status/object/item/telemetry/weather/Mic-E/third-party handling.

Higher-level decode does not replace raw evidence. Open **View → Decoded KISS / AX.25 / APRS packets...** for the dedicated packet view; parsed APRS text is not injected into the normal serial terminal output.

## BLE Notification Monitor

Open **View → BLE Notification Monitor...** for bounded notification capture with sequence, timestamp, device, service/characteristic, delivery metadata, length, HEX, ASCII-safe rendering, KISS frame count and per-characteristic counters.

Pause affects display only; capture continues. The main qualified FFE1 path is observed without creating a competing CCCD subscription.

## Diagnostic Log Filters / Search

Open **View → Diagnostic Log Filters / Search...**.

The bounded structured log supports categories such as `CONNECTION`, `DISCOVERY`, `GATT`, `CCCD`, `RX_RAW`, `TX_RAW`, `KISS`, `AX25`, `APRS`, `INSPECTOR`, `WARNING` and `ERROR`, with text/category/device/characteristic/direction filtering, navigation and full/filtered export.

Raw RX/TX entries retain binary payload bytes in addition to formatted text.

## Session Capture / Offline Replay

Open **View → Session Capture / Offline Replay...**.

Versioned `.blsession.json` captures structured events, binary RX/TX bytes, device/profile metadata and relevant GATT mapping. Offline replay feeds captured RX_RAW through independent KISS state and the normal AX.25/APRS decoders. Replay never transmits captured TX bytes to BLE.

## GATT Snapshot / Compare

The GATT Inspector can capture versioned snapshots containing services, characteristics, descriptors, normalized UUIDs, properties, discovery/access status, cached/reused/ownership metadata, Auto Detect baseline mapping and current manual route as separate state.

Snapshots can be exported to TXT, saved/reloaded as JSON and compared deterministically as A vs B.

Checklist: `PHASE_F_TEST_CHECKLIST.txt`

## KISS TCP Bridge

Open **View → KISS TCP Bridge...**.

- OFF by default
- default endpoint `127.0.0.1:8001`
- LAN/all-interface binding requires explicit confirmation
- no second BLE CCCD subscription
- raw BLE KISS bytes are forwarded unchanged to connected TCP clients
- raw TCP stream bytes are written unchanged through the current qualified BLE write route
- TCP packet boundaries are not treated as KISS frame boundaries
- bounded per-client queues prevent unbounded memory growth
- a slow/backpressured client is disconnected
- BLE disconnect or connection-context replacement closes stale TCP clients
- Auto Detect baseline write routing takes precedence over a temporary RT-950 FF31 diagnostic route

Phase G is merged and Windows CI green; real RT-950 + external KISS client testing is still required.

Checklist: `PHASE_G_TEST_CHECKLIST.txt`

## Multi-Device Live Compare — Phase H

Open **View → Multi-Device Live Compare...**.

Phase H adds a separate multi-device diagnostic ownership domain without reusing the normal MainWindow live GATT wrappers. It can scan nearby BLE devices and maintain up to four live compare sessions.

Each compare session owns its own:

- `BluetoothLEDevice`
- selected `GattDeviceService`
- Write and Notify characteristic wrappers
- GATT operation semaphore
- TX serialization semaphore
- CCCD subscription state
- `KissStreamDecoder`
- notification/RX/KISS/AX.25/APRS/TX counters
- connection generation used to reject stale queued routes
- session identity and last decoded packet state

The compare grid shows device identity, state, detected profile, service/write/notify UUIDs, CCCD readiness, counters and latest decoded packet metadata. **Compare A / Compare B** produces a deterministic live-state comparison, and KISS HEX TX can be sent to the explicitly selected session.

The architecture requirement is strict: no live Device A service/characteristic wrapper, decoder, GATT gate or TX queue may be used by Device B. A disconnected compare session can reconnect with fresh connection-scoped wrappers, while the physical BLE address currently owned by the main terminal is explicitly blocked from duplicate compare ownership. Closing the compare window disposes its compare sessions.

Phase H is merged to `main`; the authoritative Windows build run `34038127391` is green. Static isolation checks, existing regressions, all protocol/diagnostic tests, restore, Windows publish and artifact upload pass. This does **not** substitute for the required real two-device isolation test.

Authoritative Phase H main build:

```text
commit:   544605e7b4755dc77ba1440a4a555c0a1be12736
run:      34038127391
artifact: 9990834350
EXE size: 78,078,555 bytes
SHA256:   d69c3357643bc6368e05da0581688cfe5a181374fa636e0e43b69cc1a82257c9
```

Checklist: `PHASE_H_TEST_CHECKLIST.txt`

## Terminal / UI baseline

- full dark mode and dark scrollbars
- configurable terminal foreground/background
- File / View / Settings / About menus
- LF default
- Local Echo default OFF
- Enter-to-send
- 1-5 persistent TX command rows
- transparent application icon
- self-contained single-file Windows x64 publish

## Canonical Windows build

Open **Actions → Build Windows EXE**.

The workflow runs the regression suite plus phase-specific static/unit/integration tests, then performs the authoritative Windows restore and self-contained win-x64 single-file publish.

Current pipeline includes:

1. static regression checks
2. Phase F GATT Snapshot static checks
3. Phase G KISS TCP Bridge static checks
4. Phase H isolation static checks
5. Protocol tests
6. Notification tests
7. Log Filter tests
8. Session Capture/Replay tests
9. Manual RT-950 TX tests
10. GATT Snapshot tests
11. KISS TCP Bridge loopback/lifecycle tests
12. Multi-Device Compare tests
13. `dotnet restore`
14. Windows self-contained single-file publish
15. EXE SHA-256 generation
16. artifact upload

Expected artifact contents:

```text
BLESerialTerminal.exe
BLESerialTerminal.exe.sha256
```

The EXE is Windows x64 and self-contained; no separate .NET runtime installation is required.

## Phase validation files

- `RT950_TEST_CHECKLIST.txt`
- `RT950_MANUAL_GATT_TX_TEST_CHECKLIST.txt`
- `PHASE_B_TEST_CHECKLIST.txt`
- `PHASE_C_TEST_CHECKLIST.txt`
- `PHASE_D_TEST_CHECKLIST.txt`
- `PHASE_F_TEST_CHECKLIST.txt`
- `PHASE_G_TEST_CHECKLIST.txt`
- `PHASE_H_TEST_CHECKLIST.txt`
- `ROADMAP.txt`

## Local Windows publish

With the .NET 8 SDK installed:

```bat
PUBLISH_SINGLE_EXE_WIN64.bat
```

GitHub Actions on Windows remains the release authority.

## Runtime requirements

- Windows 10 2004+ or Windows 11 x64
- Bluetooth Low Energy adapter
