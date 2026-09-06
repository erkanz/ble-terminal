# BLE Serial Terminal

Native Windows BLE/GATT serial terminal and diagnostic tool for .NET 8 / WPF.

This repository is the canonical source and build location for the project.

## Current baseline

- Auto Detect BLE-UART (Nordic NUS, HM-10/FFE0-FFE1, generic UART-style GATT)
- Radtel RT-950 Pro FFE0/FFE1 support
- GATT Inspector with service/characteristic/descriptor browser
- Multi-characteristic Notify/Indicate, manual Read/Write
- Raw BLE notification logging and KISS stream reassembly
- Full dark mode, terminal color customization, export log
- Self-contained single-file Windows x64 build

## Build

GitHub Actions is the canonical build path. Every push to `main` and every manual workflow run builds the self-contained Windows x64 EXE and publishes it as a workflow artifact.

Tagged versions (`v*`) are additionally published to GitHub Releases with the EXE and SHA-256 checksum.

Local Windows build is still supported with `PUBLISH_SINGLE_EXE_WIN64.bat`.

## Requirements to run

- Windows 10 2004+ or Windows 11
- Bluetooth Low Energy adapter
- No separate .NET installation is required for the self-contained release EXE
