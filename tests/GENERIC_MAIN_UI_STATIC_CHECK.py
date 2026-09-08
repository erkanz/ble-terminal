#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
app = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
special = (ROOT / 'MainWindow.SpecialTools.cs').read_text(encoding='utf-8')
rt = (ROOT / 'Rt950ToolsWindow.cs').read_text(encoding='utf-8')
kiss = (ROOT / 'KissToolsWindow.cs').read_text(encoding='utf-8')
usb = (ROOT / 'MainWindow.UsbSerial.cs').read_text(encoding='utf-8')

checks = {
    'application no longer injects RT950 controls into main surface': 'PrepareRt950Rtx1AutoUi' not in app,
    'generic USB serial still initializes on main surface': 'PrepareUsbSerialUi' in app,
    'generic main cleanup runs after USB UI initialization': app.find('PrepareUsbSerialUi') < app.find('PrepareGenericMainUi'),
    'legacy RT950 main buttons are removed at runtime': all(x in special for x in ['RT950 OEM TEST', 'RT950 FFE1 TEST', 'Load RT950 Test Commands', 'Run RTX1 TX Test']),
    'persisted RT950 command rows are removed from generic workspace': 'RemoveLegacyRt950CommandRowsFromNormalWorkspace' in special,
    'USB RTX1 button is removed from generic USB panel': '_usbSerialRtx1Button' in special and 'RemoveFromParent(_usbSerialRtx1Button)' in special,
    'USB RTX1 functionality still exists for RT950 tool window': 'RunRt950Rtx1UsbTestAsync' in usb and 'RunRt950Rtx1UsbFromWindowAsync' in special,
    'top-level legacy RT950 and KISS menus are removed from visible main menu': 'menu.Items.Remove(_rt950Menu)' in special and 'menu.Items.Remove(_kissMenu)' in special,
    'KISS View-menu entries are removed from generic main menu': 'Decoded KISS / AX.25 / APRS packets...' in special and 'KISS TCP Bridge...' in special,
    'generic Tools launcher owns RT950 and KISS windows': 'Header = "_Tools"' in special and 'RT950 Tools...' in special and 'KISS Tools...' in special,
    'RT950 has a dedicated window': 'class Rt950ToolsWindow' in rt and 'Run RTX1 TX Test — BLE' in rt and 'Run RTX1 TX Test — USB' in rt,
    'KISS has a dedicated window': 'class KissToolsWindow' in kiss and 'KISS Frame Builder...' in kiss and 'KISS TCP Bridge...' in kiss,
    'generic main presentation suppresses device-family mode promotion': '_autoGatt.IsRadtelRt950Kiss = false' in special and 'Generic BLE-UART' in special,
    'generic About text does not advertise RT950 or KISS': 'Generic Windows BLE GATT / UART diagnostic terminal.' in special,
}

failed = [name for name, ok in checks.items() if not ok]
for name, ok in checks.items():
    print(f"{'PASS' if ok else 'FAIL'}  {name}")

if failed:
    print('\nGENERIC MAIN UI STATIC CHECK: FAIL')
    for name in failed:
        print(' -', name)
    raise SystemExit(1)

print(f"\nGENERIC MAIN UI STATIC CHECK: PASS ({len(checks)} checks)")
