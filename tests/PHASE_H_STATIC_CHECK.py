#!/usr/bin/env python3
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
models = (ROOT / 'MultiDeviceCompareModels.cs').read_text(encoding='utf-8')
session = (ROOT / 'MultiDeviceSession.cs').read_text(encoding='utf-8')
window = (ROOT / 'MultiDeviceCompareWindow.xaml.cs').read_text(encoding='utf-8')
xaml = (ROOT / 'MultiDeviceCompareWindow.xaml').read_text(encoding='utf-8')
main = (ROOT / 'MainWindow.MultiDeviceCompare.cs').read_text(encoding='utf-8')
protocol = (ROOT / 'MainWindow.Protocol.cs').read_text(encoding='utf-8')
tests = (ROOT / 'tests' / 'MultiDeviceCompareTests' / 'Program.cs').read_text(encoding='utf-8')

checks = {
    'independent live session class exists': 'sealed class MultiDeviceSession : IAsyncDisposable' in session,
    'each session owns its own GATT gate': 'private readonly SemaphoreSlim _gattOperationGate = new(1, 1);' in session,
    'each session owns its own TX queue gate': 'private readonly SemaphoreSlim _txQueueGate = new(1, 1);' in session,
    'each session owns its own KISS decoder': 'private KissStreamDecoder _kissDecoder = new();' in session,
    'each session owns device service write and notify wrappers': all(x in session for x in ['BluetoothLEDevice? _device', 'GattDeviceService? _service', 'GattCharacteristic? _writeCharacteristic', 'GattCharacteristic? _notifyCharacteristic']),
    'multi-device sessions do not reuse main AutoDetectedGattContext': 'AutoDetectedGattContext' not in session and '_autoGatt' not in session,
    'multi-device sessions do not observe main NotificationCaptureHub': 'NotificationCaptureHub' not in session,
    'CCCD uses result-bearing API': 'WriteClientCharacteristicConfigurationDescriptorWithResultAsync' in session,
    'TX uses result-bearing GATT write': 'WriteValueWithResultAsync' in session and 'GattCommunicationStatus.Success' in session,
    'TX revalidates connection generation and route': 'generation != Volatile.Read(ref _generation)' in session and '!ReferenceEquals(write, _writeCharacteristic)' in session,
    'disconnect clears connection-scoped wrappers': all(x in session for x in ['_notifyCharacteristic = null;', '_writeCharacteristic = null;', '_service = null;', '_device = null;']),
    'KISS AX25 APRS counters are per session': all(x in session for x in ['_kissFrames', '_ax25Frames', '_aprsPackets', '_notifications', '_rxBytes', '_txBytes']),
    'maximum live compare session bound exists': 'private const int MaxSessions = 4;' in window,
    'new device gets a new independent session': 'new MultiDeviceSession(item.Address, item.Name)' in window,
    'window shutdown disposes every independent session': 'await session.DisposeAsync()' in window and '_sessionsById.Clear()' in window,
    'comparison engine is snapshot based': 'MultiDeviceComparisonEngine.Compare(a.Snapshot, b.Snapshot)' in window and 'MultiDeviceSessionSnapshot' in models,
    'View menu integration exists': 'Multi-Device Live Compare...' in main and 'InitializeMultiDeviceCompare()' in protocol,
    'main shutdown closes compare window': 'ShutdownMultiDeviceCompare()' in protocol,
    'comparison model tests exist': all(x in tests for x in ['self comparison has no differences', 'different address detected', 'BLE address formatter is stable']),
    'session grid uses virtualization': 'VirtualizingPanel.VirtualizationMode="Recycling"' in xaml and 'EnableRowVirtualization="True"' in xaml,
    'UI states isolation contract explicitly': 'separate BluetoothLEDevice' in xaml and 'main terminal connection is not reused here' in xaml,
}

try:
    ET.parse(ROOT / 'MultiDeviceCompareWindow.xaml')
    checks['multi-device compare XAML parses'] = True
except Exception:
    checks['multi-device compare XAML parses'] = False

failed = [name for name, ok in checks.items() if not ok]
for name, ok in checks.items():
    print(f"{'PASS' if ok else 'FAIL'}  {name}")

if failed:
    print('\nPHASE H STATIC CHECK: FAIL')
    for name in failed:
        print(' -', name)
    raise SystemExit(1)

print(f"\nPHASE H STATIC CHECK: PASS ({len(checks)} checks)")
