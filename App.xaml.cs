using System.Windows;
using System.Windows.Threading;

namespace BLESerialTerminal;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        RuntimeDiagnostics.Write("APP_START", "Application startup");
        base.OnStartup(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (MainWindow is BLESerialTerminal.MainWindow window)
        {
            window.PrepareRt950Rtx1AutoUi();
            window.PrepareUsbSerialUi();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RuntimeDiagnostics.Write("APP_EXIT", $"Application exit code={e.ApplicationExitCode}");
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        base.OnExit(e);
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        RuntimeDiagnostics.Write("DISPATCHER_UNHANDLED", e.Exception, fatal: true);
        // Unexpected UI-thread exceptions remain fatal. Routine BLE/network paths must
        // catch and report their own failures rather than relying on a global swallow.
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            RuntimeDiagnostics.Write("APPDOMAIN_UNHANDLED", exception, fatal: e.IsTerminating);
        else
            RuntimeDiagnostics.Write("APPDOMAIN_UNHANDLED", $"Non-Exception fatal object; terminating={e.IsTerminating}");
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        RuntimeDiagnostics.Write("TASK_UNOBSERVED", e.Exception);
        e.SetObserved();
    }
}
