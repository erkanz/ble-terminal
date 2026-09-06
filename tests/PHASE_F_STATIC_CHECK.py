#!/usr/bin/env python3
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
models = (ROOT / 'GattSnapshotModels.cs').read_text(encoding='utf-8')
serializer = (ROOT / 'GattSnapshotSerializer.cs').read_text(encoding='utf-8')
comparer = (ROOT / 'GattSnapshotComparer.cs').read_text(encoding='utf-8')
window_cs = (ROOT / 'GattSnapshotWindow.xaml.cs').read_text(encoding='utf-8')
inspector = (ROOT / 'GattInspectorWindow.Snapshot.cs').read_text(encoding='utf-8')
main_route = (ROOT / 'MainWindow.GattSnapshot.cs').read_text(encoding='utf-8')
window_xaml = ROOT / 'GattSnapshotWindow.xaml'

checks = {
    'versioned structured GATT snapshot format': 'BLESerialTerminalGattSnapshot' in models and 'CurrentVersion = 1' in models,
    'snapshot stores services characteristics descriptors': all(x in models for x in ['GattSnapshotService', 'GattSnapshotCharacteristic', 'GattSnapshotDescriptor']),
    'snapshot keeps cached reused ownership metadata': all(x in models for x in ['Source', 'Reused', 'Ownership']),
    'snapshot keeps auto baseline and manual override separately': all(x in models for x in ['AutoServiceUuid', 'AutoWriteUuid', 'AutoNotifyUuid', 'ManualServiceUuid', 'ManualWriteUuid', 'ManualNotifyUuid', 'ManualOverrideActive']),
    'human and structured export exist': 'ToJson' in serializer and 'ToText' in serializer,
    'future snapshot version rejected': 'newer than this application supports' in serializer,
    'deterministic compare handles mapping access service characteristic descriptor': all(x in comparer for x in ['MAPPING', 'ACCESS', 'SERVICE', 'CHARACTERISTIC', 'DESCRIPTOR']),
    'Inspector capture reuses qualified discovery path': 'await DiscoverGattAsync()' in inspector and 'BuildGattSnapshotFromInspectorState' in inspector,
    'snapshot capture does not create a competing CCCD subscription': 'WriteClientCharacteristicConfigurationDescriptor' not in inspector,
    'RT950 auto cache metadata retained in snapshot': 'AUTO-DETECT/OWNER' in inspector and '_autoGatt' in inspector,
    'manual route snapshot comes from live MainWindow state': 'GetGattSnapshotRouteState' in main_route and 'ManualOverrideActive' in main_route,
    'snapshot compare UI supports capture load save compare export': all(x in window_cs for x in ['CaptureIntoAsync', 'LoadSnapshot', 'SaveSnapshotJson', 'SaveSnapshotText', 'CompareNow', 'ExportDiffButton_Click']),
    'snapshot compare grid virtualized': 'VirtualizingPanel.VirtualizationMode="Recycling"' in window_xaml.read_text(encoding='utf-8'),
}

try:
    ET.parse(window_xaml)
    checks['GATT snapshot XAML parses'] = True
except Exception:
    checks['GATT snapshot XAML parses'] = False

failed = [name for name, ok in checks.items() if not ok]
for name, ok in checks.items():
    print(f"{'PASS' if ok else 'FAIL'}  {name}")

if failed:
    print('\nPHASE F STATIC CHECK: FAIL')
    for name in failed:
        print(' -', name)
    raise SystemExit(1)

print('\nPHASE F STATIC CHECK: PASS')
