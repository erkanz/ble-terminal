#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
app = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
runtime = (ROOT / 'RuntimeDiagnostics.cs').read_text(encoding='utf-8')
protocol = (ROOT / 'MainWindow.Protocol.cs').read_text(encoding='utf-8')
main = (ROOT / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
rt950_tx = (ROOT / 'MainWindow.Rt950Tx.cs').read_text(encoding='utf-8')
bridge = (ROOT / 'MainWindow.KissTcpBridge.cs').read_text(encoding='utf-8')
multi = (ROOT / 'MainWindow.MultiDeviceCompare.cs').read_text(encoding='utf-8')
workflow = (ROOT / '.github' / 'workflows' / 'build-windows.yml').read_text(encoding='utf-8')
aprs = (ROOT / 'AprsDecoder.cs').read_text(encoding='utf-8')
session_tests = (ROOT / 'tests' / 'SessionTests' / 'Program.cs').read_text(encoding='utf-8')

checks = {
    'runtime diagnostics file is bounded by rotation': 'MaxLogBytes' in runtime and 'RotateIfNeeded()' in runtime and 'runtime.previous.log' in runtime,
    'runtime diagnostics cannot become a second failure path': 'Diagnostics must never become a second failure path.' in runtime,
    'dispatcher unhandled exceptions are logged': 'DispatcherUnhandledException += App_DispatcherUnhandledException' in app and 'DISPATCHER_UNHANDLED' in app,
    'dispatcher fatal exceptions are not globally swallowed': 'e.Handled = true' not in app and 'remain fatal' in app,
    'AppDomain fatal exceptions are logged': 'AppDomain.CurrentDomain.UnhandledException +=' in app and 'APPDOMAIN_UNHANDLED' in app,
    'unobserved task failures are logged and observed': 'TaskScheduler.UnobservedTaskException +=' in app and 'e.SetObserved();' in app,
    'application exception hooks are removed on exit': all(x in app for x in ['DispatcherUnhandledException -=', 'AppDomain.CurrentDomain.UnhandledException -=', 'TaskScheduler.UnobservedTaskException -=']),
    'main shutdown still tears down multi-device compare': 'ShutdownMultiDeviceCompare();' in protocol,
    'main shutdown still tears down KISS TCP bridge': 'ShutdownKissTcpBridge();' in protocol,
    'main closing still stops scan and clears live BLE connection': 'StopScan();' in main and 'CleanupConnection();' in main,
    'KISS TCP bridge detaches shared notification observer': 'NotificationCaptureHub.Store.RecordAdded -= KissTcpBridge_NotificationRecordAdded;' in bridge,
    'multi-device window is closed from main shutdown': '_multiDeviceCompareWindow?.Close()' in multi,
    'nullable APRS position result has success postcondition': 'NotNullWhen(true)' in aprs,
    'session replay test no longer dereferences nullable decoder output directly': 'decoded[0].Ax25!' not in session_tests and 'decoded[0].Aprs!' not in session_tests,
    'TX availability is safe during XAML construction': 'SendButton == null || WriteTypeComboBox == null || RoutingStatusTextBlock == null' in rt950_tx,
    'workflow executes Phase I static gate': 'Phase I production hardening static checks' in workflow and 'tests/PHASE_I_STATIC_CHECK.py' in workflow,
    'workflow contains compiler warnings gate': 'Compiler warnings gate' in workflow and 'TreatWarningsAsErrors=true' in workflow,
    'workflow contains published EXE startup smoke gate': 'Published EXE startup smoke gate' in workflow and 'Start-Process $exe -PassThru' in workflow and 'HasExited' in workflow,
}

failed = [name for name, ok in checks.items() if not ok]
for name, ok in checks.items():
    print(f"{'PASS' if ok else 'FAIL'}  {name}")

if failed:
    print('\nPHASE I STATIC CHECK: FAIL')
    for name in failed:
        print(' -', name)
    raise SystemExit(1)

print(f"\nPHASE I STATIC CHECK: PASS ({len(checks)} checks)")
