# BLE Serial Terminal

Native Windows BLE/GATT serial terminal, GATT diagnostic tool, and KISS / AX.25 / APRS analyzer built with .NET 8 / WPF.

This repository is the **canonical source and build location** for the project.

## Project status

Current baseline: **v13 RT-950 GATT Cache / FFE1 Notify Fix**

Current roadmap phase: **Phase B — KISS / AX.25 / APRS Decoder**

**Phase A is complete.** The RT-950 FFE0/FFE1 cache, CCCD, raw notification, fragmented KISS reassembly, and GATT Inspector lifecycle baseline was qualified on real hardware.

Phase B implementation and automated tests are green. The remaining Phase B gate is a real RT-950 end-to-end packet check in the new decoded-packet window.

Project continuity and phase rules are maintained in:

- `ROADMAP.txt`
- `RT950_TEST_CHECKLIST.txt`
- `PHASE_B_TEST_CHECKLIST.txt`

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

The packet viewer shows:

- timestamp
- BLE characteristic
- KISS port / command
- AX.25 source
- AX.25 destination
- path
- APRS / frame type
- summary
- full KISS raw + unescaped HEX
- AX.25 control/PID/information
- original APRS information
- decoded APRS fields and warnings

Packet details can be copied or exported. The decoded packet history is bounded so it does not grow indefinitely.

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

GitHub Actions is the primary build path. Every push to `main`, pull request, and manual workflow run executes regression checks, Phase B protocol tests, and builds the Windows x64 application on a real Windows runner.

Open:

**Actions → Build Windows EXE**

The workflow produces:

```text
BLESerialTerminal.exe
BLESerialTerminal.exe.sha256
```

The EXE is:

- Windows x64
- self-contained
- single-file
- no separate .NET runtime installation required on the target PC

For tagged versions (`v*`), the same EXE and checksum are automatically attached to a GitHub Release.

## Phase B validation

Use the newest successful `main` Actions artifact and follow:

```text
PHASE_B_TEST_CHECKLIST.txt
```

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

See `RT950_TEST_CHECKLIST.txt`, `PHASE_A_RT950_SESSION_2026-09-06.txt`, `PHASE_B_TEST_CHECKLIST.txt`, and `V13_RT950_GATT_CACHE_FIX.txt` for current diagnostics and regression expectations.
