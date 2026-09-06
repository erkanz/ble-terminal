#!/usr/bin/env python3
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
main = (ROOT / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
insp = (ROOT / 'GattInspectorWindow.xaml.cs').read_text(encoding='utf-8')
models = (ROOT / 'GattInspectorModels.cs').read_text(encoding='utf-8')
ctx = (ROOT / 'AutoDetectedGattContext.cs').read_text(encoding='utf-8')

checks = {
    'auto cache context exists': 'class AutoDetectedGattContext' in ctx,
    'cache retains full selected-service characteristic list': 'ServiceCharacteristics' in ctx and 'bestServiceCharacteristics' in main,
    'main uses result-bearing CCCD write': 'WriteClientCharacteristicConfigurationDescriptorWithResultAsync(cccd)' in main,
    'FFE1 handler attach log exists': 'FFE1 VALUECHANGED HANDLER ATTACHED' in main,
    'RT950 profile marker exists': 'RADTEL_RT950_KISS' in main,
    'raw notification log exists': 'RAW BLE NOTIFICATION' in main,
    'raw log precedes main KISS parser': main.find('AppendSystemLine("RAW BLE NOTIFICATION")') < main.find('_autoKissDecoder.Push(data)'),
    'inspector receives auto cache': 'AutoDetectedGattContext? autoGatt' in insp,
    'inspector shares GATT operation gate': 'SemaphoreSlim gattOperationGate' in insp and '_gattOperationGate = gattOperationGate;' in insp,
    'inspector reuses auto service': 'AddAutoDetectedServiceAsync' in insp and 'AUTO-DETECT/REUSED' in insp,
    'inspector skips second FFE0 characteristic enumeration': 'FFE0 ENUMERATION SKIPPED TO AVOID SECOND GetCharacteristicsAsync' in insp,
    'inspector reports cached FFE1': 'FFE1 present: YES (from Auto Detect cache)' in insp,
    'inspector does not own cached service': 'OwnsService = false' in insp and 'if (!service.OwnsService)' in insp,
    'inspector can reuse active CCCD': 'AUTO CCCD REUSED' in insp,
    'inspector raw notification log precedes KISS parser': insp.find('Log("*** RAW BLE NOTIFICATION")') < insp.find('decoder.Push(data)'),
    'source/reused UI metadata exists': 'Source: {info.Source}' in insp and 'Reused: {(info.Reused ? "Yes" : "No")}' in insp,
    'ownership metadata model exists': 'OwnsService' in models and 'Reused' in models and 'Source' in models,
}

for xaml in ROOT.glob('*.xaml'):
    try:
        ET.parse(xaml)
        checks[f'XAML parse {xaml.name}'] = True
    except Exception:
        checks[f'XAML parse {xaml.name}'] = False

failed = [name for name, ok in checks.items() if not ok]
for name, ok in checks.items():
    print(f"{'PASS' if ok else 'FAIL'}  {name}")

if failed:
    print('\nSTATIC REGRESSION CHECK: FAIL')
    for name in failed:
        print(' -', name)
    raise SystemExit(1)

print('\nSTATIC REGRESSION CHECK: PASS')
