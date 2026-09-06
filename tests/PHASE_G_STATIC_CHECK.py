#!/usr/bin/env python3
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
server = (ROOT / 'KissTcpBridgeServer.cs').read_text(encoding='utf-8')
window = (ROOT / 'KissTcpBridgeWindow.xaml.cs').read_text(encoding='utf-8')
xaml = (ROOT / 'KissTcpBridgeWindow.xaml').read_text(encoding='utf-8')
main = (ROOT / 'MainWindow.KissTcpBridge.cs').read_text(encoding='utf-8')
protocol = (ROOT / 'MainWindow.Protocol.cs').read_text(encoding='utf-8')
tests = (ROOT / 'tests' / 'KissTcpBridgeTests' / 'Program.cs').read_text(encoding='utf-8')

checks = {
    'bridge is explicit and not auto-started': 'Start bridge' in xaml and 'StartAsync(new KissTcpBridgeOptions' in window and 'StartAsync(' not in main,
    'default binding is localhost': 'Localhost only (127.0.0.1)' in xaml and 'SelectedIndex="0"' in xaml and 'IPAddress.Loopback' in window,
    'LAN binding needs explicit confirmation': 'LAN / all interfaces (0.0.0.0)' in xaml and 'MessageBoxButton.YesNo' in window and 'IPAddress.Any' in window,
    'closing bridge window stops listener': 'Closing this window stops the listener' in xaml and 'await _server.StopAsync()' in window,
    'bounded per-client send queue exists': 'Channel.CreateBounded<byte[]>' in server and 'BoundedChannelFullMode.Wait' in server and 'TryWrite(data)' in server,
    'backpressure disconnect is explicit': 'bounded BLE->TCP queue full' in server and '_backpressureDisconnects' in server and 'session.Close()' in server,
    'BLE RX bytes are forwarded unchanged': 'BroadcastBleRx' in server and 'byte[] immutable = data.ToArray()' in server and 'session.TryQueue(immutable)' in server,
    'TCP bytes are forwarded unchanged to BLE callback': '_bleWriter(data, cancellationToken)' in server,
    'TCP stream KISS decoder is diagnostic only': 'TCP byte boundaries are deliberately not treated as KISS' in server and 'session.Decode(data)' in server,
    'BLE stream KISS diagnostics preserve fragmentation': '_bleRxDecoder.Push(immutable)' in server,
    'multiple clients are supported': 'ConcurrentDictionary<long, ClientSession>' in server and '_clients.Values' in server,
    'BLE disconnect/reconnect drops stale TCP clients': 'BLE connection context changed' in server and 'CloseAllClients' in server and '_bleConnectionKey' in server,
    'main bridge observes existing notification capture path': 'NotificationCaptureHub.Store.RecordAdded +=' in main and 'KissTcpBridge_NotificationRecordAdded' in main,
    'Inspector-only notification duplication is excluded': 'AUTO-DETECT' in main and 'MANUAL' in main and 'Inspector-only' in main,
    'bridge TX prefers Auto Detect baseline route': '_autoGatt.IsAvailable' in main and '_autoGatt.WriteCharacteristic' in main and 'temporary RT-950 FF31 diagnostic route does not redirect' in xaml,
    'bridge TX shares GATT serialization gate': '_gattOperationGate.WaitAsync(cancellationToken)' in main,
    'bridge TX revalidates connection scoped route': 'BLE connection/route changed before the queued write executed' in main and 'ReferenceEquals(currentBridgeWrite, write)' in main,
    'bridge TX uses result-bearing GATT write': 'WriteValueWithResultAsync' in main and 'GattCommunicationStatus.Success' in main,
    'successful bridge TX enters structured TX_RAW log': 'LogCategory.TX_RAW' in main and 'KISS TCP BRIDGE BLE WRITE' in main,
    'bridge lifecycle integrated with MainWindow': 'InitializeKissTcpBridge()' in protocol and 'ShutdownKissTcpBridge()' in protocol,
    'bridge menu item is installed under View': 'KISS TCP Bridge...' in main and 'InstallKissTcpBridgeMenuItem' in main,
    'bridge UI exposes endpoint/client/byte/frame/backpressure counters': all(x in xaml for x in ['EndpointTextBlock', 'ClientsTextBlock', 'BleRxTextBlock', 'TcpRxTextBlock', 'BackpressureTextBlock']),
    'bridge event UI is bounded and virtualized': 'MaxEventRows = 2000' in window and 'VirtualizingPanel.VirtualizationMode="Recycling"' in xaml,
    'loopback network tests cover RX TX multi-client reconnect': all(x in tests for x in ['BLE->TCP preserves raw KISS bytes exactly', 'TCP stream bytes reach BLE writer unchanged', 'multiple clients stay independent', 'fresh BLE connection context']),
}

try:
    ET.parse(ROOT / 'KissTcpBridgeWindow.xaml')
    checks['KISS TCP bridge XAML parses'] = True
except Exception:
    checks['KISS TCP bridge XAML parses'] = False

failed = [name for name, ok in checks.items() if not ok]
for name, ok in checks.items():
    print(f"{'PASS' if ok else 'FAIL'}  {name}")

if failed:
    print('\nPHASE G STATIC CHECK: FAIL')
    for name in failed:
        print(' -', name)
    raise SystemExit(1)

print(f"\nPHASE G STATIC CHECK: PASS ({len(checks)} checks)")
