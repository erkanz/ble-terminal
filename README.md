# BLE Serial Terminal

Native Windows BLE/GATT serial terminal and diagnostic tool built with .NET 8 / WPF.

This repository is the **canonical source and build location** for the project.

## Project status

Current baseline: **v13 RT-950 GATT Cache / FFE1 Notify Fix**

Current roadmap phase: **Phase A — v13 Hardware Qualification + Baseline Freeze**

Automated Windows CI is green; real RT-950 hardware qualification is the remaining gate before Phase B begins.

Project continuity and phase rules are maintained in:

- `ROADMAP.txt`
- `PHASE_A_HARDWARE_QUALIFICATION.txt`
- `RT950_TEST_CHECKLIST.txt`

## Current baseline

- Auto Detect BLE-UART
  - Nordic UART Service (NUS)
  - HM-10 / FFE0-FFE1
  - generic UART-style GATT profiles
- Radtel RT-950 Pro FFE0/FFE1 KISS profile support
- Cached Auto Detect GATT objects to avoid RT-950 FFE0 re-enumeration / false `AccessDenied` negatives
- Real CCCD Notify subscription result logging
- Raw BLE notification logging before KISS parsing
- GATT Inspector
  - all services / characteristics / descriptors
  - characteristic properties
  - manual Read / Write
  - multi-characteristic Notify / Indicate
  - source/reuse metadata for cached Auto Detect objects
- Per-characteristic KISS stream reassembly with escape handling
- Full dark mode and dark scrollbars
- Configurable serial output foreground/background colors
- File / View / About menus and Export Log
- LF default line ending
- Local echo default off
- Enter-to-send
- Transparent multi-resolution application icon
- Self-contained single-file Windows x64 publish

## Canonical Windows build

GitHub Actions is the primary build path. Every push to `main`, pull request, and manual workflow run executes the regression checks and builds the Windows x64 application on a real Windows runner.

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

## Phase A hardware qualification

For the current baseline, test the EXE downloaded from the newest successful `main` GitHub Actions run.

Follow:

```text
PHASE_A_HARDWARE_QUALIFICATION.txt
RT950_TEST_CHECKLIST.txt
```

Do not qualify Phase A with an old local ZIP/build. A green CI build is not considered proof of BLE/GATT hardware behavior; the RT-950 FFE0/FFE1 runtime checks must also pass before Phase A is marked complete.

## Local Windows publish

With .NET 8 SDK installed:

```bat
PUBLISH_SINGLE_EXE_WIN64.bat
```

## Ubuntu cross-build

The historical Ubuntu cross-publish helper remains in the repository for compatibility, but GitHub Actions on `windows-latest` is now the authoritative release build environment.

## Runtime requirements

- Windows 10 2004+ or Windows 11 x64
- Bluetooth Low Energy adapter

## RT-950 Pro debug target

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

See `RT950_TEST_CHECKLIST.txt`, `PHASE_A_HARDWARE_QUALIFICATION.txt`, and `V13_RT950_GATT_CACHE_FIX.txt` for current diagnostics and regression expectations.
