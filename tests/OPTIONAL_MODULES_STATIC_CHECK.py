#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
features = (ROOT / 'MainWindow.OptionalFeatures.cs').read_text(encoding='utf-8')
feature_models = (ROOT / 'OptionalFeatureModels.cs').read_text(encoding='utf-8')
commands = (ROOT / 'MainWindow.Rt950Tx.cs').read_text(encoding='utf-8')
command_models = (ROOT / 'GattRoutingModels.cs').read_text(encoding='utf-8')
notifications = (ROOT / 'MainWindow.Notifications.cs').read_text(encoding='utf-8')
protocol = (ROOT / 'MainWindow.Protocol.cs').read_text(encoding='utf-8')
kiss = (ROOT / 'KissStreamDecoder.cs').read_text(encoding='utf-8')
legacy_gate = (ROOT / 'MainWindow.Rt950Tx.CccdGate.cs').read_text(encoding='utf-8')
main = (ROOT / 'MainWindow.xaml.cs').read_text(encoding='utf-8')

checks = {
    'general BLE terminal defaults RT950 tools off': 'public bool Rt950ToolsEnabled { get; set; }' in feature_models and 'Rt950ToolsEnabled { get; set; } = true' not in feature_models,
    'general BLE terminal defaults KISS tools off': 'public bool KissToolsEnabled { get; set; }' in feature_models and 'KissToolsEnabled { get; set; } = true' not in feature_models,
    'top-level RT950 and KISS menus are independent': 'Header = "_RT950"' in features and 'Header = "_KISS"' in features,
    'RT950 enable switch exists': 'Enable RT950 Tools' in features and 'Rt950EnableMenuItem_Click' in features,
    'KISS enable switch exists': 'Enable KISS Tools' in features and 'KissEnableMenuItem_Click' in features,
    'RT950 auto unlock switch exists': 'Auto Unlock On Connect' in features and 'Rt950AutoUnlockOnConnect' in feature_models,
    'RT950 state machine includes verified sequence states': all(x in feature_models for x in ['ServicesDiscovered', 'Ffe1NotifyEnabling', 'Ffe1NotifyReady', 'Ff31UnlockWrite', 'WaitUnlockResponse', 'Ready']),
    'RT950 unlock fixture is exact 20-byte protocol constant': '0x3F, 0x3F, 0x3F, 0x3F, 0x02, 0x2E, 0x17, 0x1D, 0x5E, 0x57' in feature_models and '0x25, 0x2F, 0x57, 0x13, 0x62, 0x56, 0x04, 0x4B, 0x23, 0x42' in feature_models,
    'RT950 unlock writes FF31 with response': 'UUID=FF31' in features and 'GattWriteOption.WriteWithResponse' in features and 'RT950 UNLOCK WRITE SUCCESS' in features,
    'RT950 normal data route is FFE1': 'DATA_WRITE_UUID=FFE1' in features and 'WRITE=FFE1' in features and 'UNLOCK_WRITE=FF31' in features,
    'RT950 unlock failure retains BLE connection': 'generic BLE connection retained' in features and 'CleanupConnection()' not in features,
    'RT950 unlock response is classified without swallowing raw capture': 'CLASSIFICATION=RT950_UNLOCK_RESPONSE' in features and 'NotificationRecord record = CaptureMainNotification(sender, data);' in notifications,
    'RT950 unlock command rows validate current data before canonical dispatch': 'ValidateRt950UnlockCommandSource' in features and 'TxPayloadBuilder.Build(row.Command' in features and 'EMPTY_COMMAND_DATA' in features and 'RT950_UNLOCK_COMMAND_DATA_MISMATCH' in features and features.find('if (!ValidateRt950UnlockCommandSource(source))') < features.find('if (IsRt950DataPathReady)'),
    'OEM handshake fixture is separated from unlock': 'PROGRAMBT9000U' not in features and 'Rt950Protocol.OemHandshake' in commands and 'TargetOverride = "FFE1"' in commands,
    'OEM ACK 06 detection is RT950 optional-module behavior': 'RT950 OEM ACK RECEIVED: 06' in features and 'IsOemAck' in feature_models,
    'FF31 safeguard applies only while RT950 tools enabled': '_optionalFeatureSettings.Rt950ToolsEnabled && BleUuid.Is(characteristic.Uuid, "FF31")' in commands and 'RT950_FF31_RESERVED_FOR_UNLOCK' in commands,
    'old global RT950 CCCD mouse gate is retired': 'OnPreviewMouseDown' not in legacy_gate and 'old implementation' in legacy_gate,
    'command workspace has runtime add command control': '+ ADD COMMAND' in commands and 'AddCommandRow' in commands,
    'command workspace has no 1..5 clamp': '_txCommandRowCount' not in commands and 'Math.Clamp' not in commands,
    'each dynamic command row has exact SEND CLR and remove controls': 'Content = "SEND"' in commands and 'Content = "CLR"' in commands and 'Content = "X"' in commands,
    'CLR only clears command data': 'row.Command = string.Empty' in commands and 'data.Clear();' in commands,
    'command rows persist labels data order and overrides': all(x in command_models for x in ['TargetOverride', 'WriteTypeOverride', 'TxModeOverride', 'LineEndingOverride']) and 'CommandWorkspaceSettings' in commands and 'SaveTxCommandPreferences' in commands,
    'per-row discovered writable target override exists': 'RefreshCommandTargetChoices' in commands and 'IsWritableCharacteristic' in commands,
    'separate command queue item identity is retained': 'COMMAND_ID=' in commands and 'COMMAND_LABEL=' in commands and '_manualTxQueue.Enqueue' in commands,
    'KISS main RX parsing is feature gated and not RT950 gated': 'if (IsKissRxProcessingEnabled)' in notifications and '_autoGatt.IsRadtelRt950Kiss' not in notifications,
    'raw notification capture happens before optional KISS parse': notifications.find('NotificationRecord record = CaptureMainNotification(sender, data);') < notifications.find('_notificationMonitorKissDecoder.Push(data)'),
    'retired embedded RT950 KISS decoder is blocked by policy': 'ReferenceEquals(decoder, _autoKissDecoder)' in features and 'return false;' in features and 'ProcessingPolicy' in kiss,
    'decoded AX25/APRS pipeline listens to independent KISS decoder': 'ReferenceEquals(decoder, _notificationMonitorKissDecoder)' in protocol and 'IsKissDecodedDisplayEnabled' in protocol,
    'KISS raw frames remain optional annotations': 'Show Raw KISS Frames' in features and 'LogKissFeatureFrame' in features,
    'KISS frame builder has standard FEND FESC escapes': all(x in feature_models for x in ['0xC0', '0xDB', '0xDC', '0xDD']) and 'BuildDataFrame' in feature_models,
    'generic terminal RX append remains unconditional': 'AppendRx(data);' in main,
    'RT950 and KISS settings persist independently': 'optional-features.json' in features and 'SaveOptionalFeatureSettings' in features,
}

failed = [name for name, ok in checks.items() if not ok]
for name, ok in checks.items():
    print(f"{'PASS' if ok else 'FAIL'}  {name}")

if failed:
    print('\nOPTIONAL MODULES STATIC CHECK: FAIL')
    for name in failed:
        print(' -', name)
    raise SystemExit(1)

print(f"\nOPTIONAL MODULES STATIC CHECK: PASS ({len(checks)} checks)")
