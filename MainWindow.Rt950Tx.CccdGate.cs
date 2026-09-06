namespace BLESerialTerminal;

public partial class MainWindow
{
    // Intentionally empty compatibility partial.
    //
    // The old implementation intercepted every command-row click/Enter whenever an
    // auto-detected RT950 FFE1 subscription was not ready. That made generic terminal
    // behavior depend on RT950 detection and also encoded the retired 1..5 row limit.
    //
    // The modular architecture now applies readiness/unlock gates only to commands
    // explicitly tagged as RT950 diagnostics. Generic GATT writes remain user-controlled.
}
