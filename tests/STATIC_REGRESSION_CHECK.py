#!/usr/bin/env python3
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
main = (ROOT / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
main_protocol = (ROOT / 'MainWindow.Protocol.cs').read_text(encoding='utf-8')
main_notifications = (ROOT / 'MainWindow.Notifications.cs').read_text(encoding='utf-8')
main_logs = (ROOT / 'MainWindow.Logs.cs').read_text(encoding='utf-8')
main_session = (ROOT / 'MainWindow.Session.cs').read_text(encoding='utf-8')
insp = (ROOT / 'GattInspectorWindow.xaml.cs').read_text(encoding='utf-8')
insp_notifications = (ROOT / 'GattInspectorWindow.Notifications.cs').read_text(encoding='utf-8')
models = (ROOT / 'GattInspectorModels.cs').read_text(encoding='utf-8')
ctx = (ROOT / 'AutoDetectedGattContext.cs').read_text(encoding='utf-8')
kiss = (ROOT / 'KissStreamDecoder.cs').read_text(encoding='utf-8')
ax25 = (ROOT / 'Ax25Decoder.cs').read_text(encoding='utf-8')
aprs = (ROOT / 'AprsDecoder.cs').read_text(encoding='utf-8')
notifications = (ROOT / 'NotificationCapture.cs').read_text(encoding='utf-8')
notification_window = (ROOT / 'NotificationMonitorWindow.xaml.cs').read_text(encoding='utf-8')
notification_xaml = (ROOT / 'NotificationMonitorWindow.xaml').read_text(encoding='utf-8')
structured_log = (ROOT / 'StructuredLog.cs').read_text(encoding='utf-8')
log_filter_engine = (ROOT / 'LogFilterEngine.cs').read_text(encoding='utf-8')
log_filter_window = (ROOT / 'LogFilterWindow.xaml.cs').read_text(encoding='utf-8')
log_filter_xaml = (ROOT / 'LogFilterWindow.xaml').read_text(encoding='utf-8')
session_capture = (ROOT / 'SessionCapture.cs').read_text(encoding='utf-8')
session_replay = (ROOT / 'SessionReplayProcessor.cs').read_text(encoding='utf-8')
session_window = (ROOT / 'SessionCaptureWindow.xaml.cs').read_text(encoding='utf-8')
session_xaml = (ROOT / 'SessionCaptureWindow.xaml').read_text(encoding='utf-8')
main_xaml = (ROOT / 'MainWindow.xaml').read_text(encoding='utf-8')

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
    'inspector does not own cached service': 'OwnsService = false' in insp and 'OwnsService' in models,
    'inspector can reuse active CCCD': 'AUTO CCCD REUSED' in insp,
    'inspector raw notification log precedes KISS parser': insp.find('Log("*** RAW BLE NOTIFICATION")') < insp.find('decoder.Push(data)'),
    'source/reused UI metadata exists': 'Source: {info.Source}' in insp and 'Reused: {(info.Reused ? "Yes" : "No")}' in insp,
    'ownership metadata model exists': 'OwnsService' in models and 'Reused' in models and 'Source' in models,

    # Phase B protocol pipeline guards.
    'structured KISS model exposes port and command': 'public int? Port' in kiss and 'public int? Command' in kiss,
    'KISS malformed escape warnings preserved': 'Unknown KISS escape sequence' in kiss and 'Warnings' in kiss,
    'AX25 decoder exists': 'static class Ax25Decoder' in ax25 and 'TryDecode' in ax25,
    'AX25 KISS FCS policy documented': 'Not supplied by KISS transport' in (ROOT / 'ProtocolModels.cs').read_text(encoding='utf-8'),
    'APRS decoder exists': 'static class AprsDecoder' in aprs and 'DecodePosition' in aprs and 'DecodeMessage' in aprs,
    'main Phase B decoder is isolated from Inspector decoders': 'ReferenceEquals(decoder, _autoKissDecoder)' in main_protocol,
    'main protocol processing is queued after KISS logging': 'Dispatcher.BeginInvoke(() => ProcessDecodedKissFrame' in main_protocol,
    'decoded packet window integration exists': 'DecodedPacketsMenuItem_Click' in main_protocol and 'DecodedPacketsWindow' in main_protocol,

    # Phase C notification monitor guards.
    'notification capture store is bounded': 'RemoveRange(0, _history.Count - _maxHistory)' in notifications,
    'notification sequence is monotonic': 'Interlocked.Increment(ref _sequence)' in notifications,
    'notification counters are per characteristic': '_byCharacteristic' in notifications and 'ByCharacteristic' in notifications,
    'main capture uses independent ValueChanged handler': 'NotificationCaptureCharacteristic_ValueChanged' in main_notifications and 'target.ValueChanged += NotificationCaptureCharacteristic_ValueChanged' in main_notifications,
    'main monitor KISS decoder is isolated': '_notificationMonitorKissDecoder' in main_notifications and 'ReferenceEquals(decoder, _autoKissDecoder)' in main_protocol,
    'notification monitor is available from View menu': 'BLE Notification Monitor...' in main_xaml and 'NotificationMonitorMenuItem_Click' in main_notifications,
    'notification monitor pause does not stop capture': 'if (!_paused)' in notification_window and 'PAUSED (capture continues)' in notification_window,
    'notification UI history is bounded': 'MaxVisibleRows = 5000' in notification_window,
    'notification grid uses virtualization': 'VirtualizingPanel.VirtualizationMode="Recycling"' in notification_xaml,
    'inspector multi-characteristic capture follows active subscriptions': '_subscribedCharacteristics.ToHashSet()' in insp_notifications and 'NotificationCaptureCharacteristic_ValueChanged' in insp_notifications,
    'inspector auto terminal notifications are not double counted': 'Do not double-count the same FFE1 notification' in insp_notifications,
    'notification capture failures are isolated from BLE RX': 'must never interfere with the terminal RX path' in main_notifications and 'must never alter Inspector notification behavior' in insp_notifications,

    # Phase D structured log / filters / search guards.
    'structured log store is bounded': 'RemoveRange(0, _history.Count - _maxHistory)' in structured_log and '50000' in structured_log,
    'structured log has required categories': all(x in structured_log for x in ['CONNECTION', 'DISCOVERY', 'GATT', 'CCCD', 'RX_RAW', 'TX_RAW', 'KISS', 'AX25', 'APRS', 'INSPECTOR', 'WARNING', 'ERROR']),
    'structured log can carry raw payload bytes': 'byte[] Data' in structured_log and 'byte[]? data = null' in structured_log,
    'Phase D captures BLE notifications independently': 'NotificationCaptureHub.Store.RecordAdded +=' in main_logs and 'LogCategory.RX_RAW' in main_logs,
    'Phase D captures successful TX independently of local echo': 'CaptureSuccessfulTxAsync' in main_logs and 'LogCategory.TX_RAW' in main_logs,
    'Phase D stores raw RX and TX bytes': 'data: record.Data' in main_logs and 'data: payload' in main_logs,
    'Phase D does not duplicate RT950 raw terminal rendering': '_rawTerminalMetadataLinesToSkip = 3' in main_logs,
    'Phase D log filter engine supports text category device characteristic direction': all(x in log_filter_engine for x in ['criteria.Text', 'criteria.Category', 'criteria.Device', 'criteria.Characteristic', 'criteria.Direction']),
    'Phase D errors warnings only filter exists': 'ErrorsWarningsOnly' in log_filter_engine and 'ErrorsWarningsOnlyCheckBox' in log_filter_window,
    'Phase D search navigation disables auto-scroll': 'AutoScrollCheckBox.IsChecked = false' in log_filter_window and 'MoveSelection' in log_filter_window,
    'Phase D filtered and full exports exist': 'Export filtered diagnostic log' in log_filter_window and 'Export full diagnostic log' in log_filter_window,
    'Phase D export includes session metadata': 'SESSION METADATA' in log_filter_window and 'BuildLogSessionMetadata' in main_logs,
    'Phase D log window is available from View menu': 'Diagnostic Log Filters / Search...' in main_xaml and 'LogFilterMenuItem_Click' in main_logs,
    'Phase D log grid uses virtualization': 'VirtualizingPanel.VirtualizationMode="Recycling"' in log_filter_xaml,
    'Phase D visible history is bounded': 'MaxVisibleRows = 20000' in log_filter_window,

    # Phase E session capture / replay guards.
    'Phase E versioned session format exists': 'SessionFormat' in session_capture and 'CurrentVersion = 1' in session_capture and 'BLESerialTerminalSession' in session_capture,
    'Phase E session captures structured LogStore entries': 'class SessionRecorder' in session_capture and '_store.EntryAdded += Store_EntryAdded' in session_capture,
    'Phase E session raw bytes are not terminal-text-only': 'DataBase64' in session_capture and 'DataHex' in session_capture and 'FromLogEntry' in session_capture,
    'Phase E session metadata includes profile and GATT mapping': all(x in session_capture for x in ['Profile', 'ServiceUuid', 'WriteUuid', 'NotifyUuid', 'RelevantGatt']),
    'Phase E session metadata comes from live main connection': 'BuildSessionCaptureMetadata' in main_session and 'ServiceCharacteristics' in main_session,
    'Phase E serializer validates format versions': 'MinimumSupportedVersion' in session_capture and 'newer than this application supports' in session_capture,
    'Phase E replay uses per-characteristic KISS decoder state': 'Dictionary<string, KissStreamDecoder>' in session_replay,
    'Phase E replay feeds only RX_RAW RX byte events': 'LogCategory.RX_RAW' in session_replay and 'record.Direction.Equals("RX"' in session_replay,
    'Phase E replay reuses AX25 and APRS decoders': 'Ax25Decoder.TryDecode' in session_replay and 'AprsDecoder.Decode' in session_replay,
    'Phase E replay reset clears stream state': '_kissDecoders.Clear()' in session_replay and 'public void Reset()' in session_replay,
    'Phase E replay UI explicitly blocks BLE TX': 'NEVER transmitted to BLE' in session_xaml and 'BLE TX DISABLED' in session_window,
    'Phase E capture and replay cannot run together': 'Stop live capture before starting offline replay' in session_window and 'Pause offline replay before starting a live session capture' in session_window,
    'Phase E replay controls exist': all(x in session_xaml for x in ['PlayButton', 'PauseButton', 'StepButton', 'ResetReplayButton', 'ReplaySpeedComboBox']),
    'Phase E session UI is available from View menu': 'Session Capture / Offline Replay...' in main_xaml and 'SessionCaptureMenuItem_Click' in main_session,
    'Phase E replay grids use virtualization': session_xaml.count('VirtualizingPanel.VirtualizationMode="Recycling"') >= 2,
    'Phase E session window closes with main app': 'CloseSessionCaptureWindow()' in main_protocol,
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
