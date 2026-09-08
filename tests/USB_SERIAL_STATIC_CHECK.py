#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
usb = (ROOT / 'MainWindow.UsbSerial.cs').read_text(encoding='utf-8')
transport = (ROOT / 'UsbSerialTransport.cs').read_text(encoding='utf-8')
app = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
project = (ROOT / 'BLESerialTerminal.csproj').read_text(encoding='utf-8')
rtx1 = (ROOT / 'MainWindow.Rt950Rtx1.cs').read_text(encoding='utf-8')

checks = {
    'BLE scanned devices are wrapped in a collapsible expander': 'Scanned BLE Devices' in usb and 'Expander' in usb and 'DeviceGrid.Parent is not Grid root' in usb,
    'USB serial COM port enumeration exists': 'SerialPort.GetPortNames()' in transport and 'RefreshUsbSerialPorts' in usb,
    'USB serial connect and disconnect exist': '_usbSerial.Open(portName, baudRate)' in usb and '_usbSerial?.Close()' in usb,
    'USB serial raw RX is appended to terminal': 'RAW USB SERIAL RX' in usb and 'AppendRx(data);' in usb,
    'USB serial raw ASCII HEX TX exists': 'TxPayloadBuilder.Build(_usbRawTxTextBox.Text' in usb and 'RAW USB SERIAL TX' in usb,
    'USB RTX1 action is exposed': 'Run RTX1 TX Test over USB' in usb and 'RunRt950Rtx1UsbTestAsync' in usb,
    'USB RTX1 reuses verified AX25 FCS packet builder': 'Rt950Rtx1Protocol.BuildRtx1Packet' in usb and 'Rt950Rtx1Protocol.VerifyFcs' in usb,
    'USB RTX1 is direct serial stream not fake GATT routing': 'WRITE_MODE=RAW_BINARY_STREAM' in usb and 'BLE_HANDSHAKE=NOT_APPLICABLE' in usb and 'FF31' not in usb,
    'USB transport success does not claim RF success': 'USB_SERIAL_TRANSPORT=' in usb and 'RTX1_DEVICE_ACK=NOT_SEEN' in usb and 'RF_TX=UNKNOWN' in usb,
    'BLE and USB RTX1 share one concurrency gate': '_rtx1TestInProgress' in usb and '_rtx1TestInProgress' in rtx1,
    'System.IO.Ports package is referenced': 'System.IO.Ports' in project,
    'USB serial UI initializes on application activation': 'PrepareUsbSerialUi' in app,
}

failed = [name for name, ok in checks.items() if not ok]
for name, ok in checks.items():
    print(f"{'PASS' if ok else 'FAIL'}  {name}")

if failed:
    print('\nUSB SERIAL STATIC CHECK: FAIL')
    for name in failed:
        print(' -', name)
    raise SystemExit(1)

print(f"\nUSB SERIAL STATIC CHECK: PASS ({len(checks)} checks)")
