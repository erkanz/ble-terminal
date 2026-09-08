using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private bool _strictGenericMainUiPrepared;
    private bool _genericTerminalSanitizing;
    private SpecialToolLogScope _specialToolLogScope;
    private int _kissRawBlockLinesRemaining;

    private enum SpecialToolLogScope
    {
        None,
        Rt950,
        Kiss
    }

    internal void PrepareStrictGenericMainUi()
    {
        // Run before PrepareGenericMainUi so old persisted RT950 presets disappear silently.
        // This prevents migration/cleanup details from leaking device-specific wording onto
        // the generic terminal surface.
        RemovePersistedRt950CommandsSilently();

        if (_strictGenericMainUiPrepared)
            return;

        _strictGenericMainUiPrepared = true;
        TerminalTextBox.TextChanged += GenericTerminal_TextChanged;
        Rt950ToolActivity += StrictGeneric_Rt950ToolActivity;
        KissToolActivity += StrictGeneric_KissToolActivity;
        GattInspectorButton.IsEnabledChanged += StrictGeneric_ConnectionStateChanged;
        SanitizeGenericTerminal();
    }

    private void RemovePersistedRt950CommandsSilently()
    {
        int removed = _commandRows.RemoveAll(row =>
            string.Equals(row.ModuleTag, "RT950", StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return;

        if (_commandRows.Count == 0)
            _commandRows.Add(NewGeneralCommand("Command 1"));
        RebuildDynamicCommandRows();
        SaveTxCommandPreferences();
    }

    private void StrictGeneric_Rt950ToolActivity(string message)
    {
        if (message.Contains("Applying RT950 GATT preset", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Starting RT950 BLE unlock", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Starting RTX1 BLE test", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Starting RTX1 USB Serial test", StringComparison.OrdinalIgnoreCase))
        {
            _specialToolLogScope = SpecialToolLogScope.Rt950;
            return;
        }

        if (message.Contains("GATT preset action completed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("RT950 unlock state", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("RTX1 BLE test completed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("RTX1 USB Serial test completed", StringComparison.OrdinalIgnoreCase))
        {
            _specialToolLogScope = SpecialToolLogScope.None;
            _ = Dispatcher.BeginInvoke(SanitizeGenericTerminal, DispatcherPriority.ContextIdle);
        }
    }

    private void StrictGeneric_KissToolActivity(string message)
    {
        // Window-launch/configuration activity is already displayed in KISS Tools. Keep the
        // generic terminal free of protocol-module presentation state.
        if (message.Contains("KISS", StringComparison.OrdinalIgnoreCase))
            _ = Dispatcher.BeginInvoke(SanitizeGenericTerminal, DispatcherPriority.ContextIdle);
    }

    private void StrictGeneric_ConnectionStateChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e) =>
        _ = Dispatcher.BeginInvoke(SanitizeGenericTerminal, DispatcherPriority.ContextIdle);

    private void GenericTerminal_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_genericTerminalSanitizing)
            return;
        SanitizeGenericTerminal();
    }

    private void SanitizeGenericTerminal()
    {
        if (_genericTerminalSanitizing || TerminalTextBox == null || string.IsNullOrEmpty(TerminalTextBox.Text))
            return;

        string original = TerminalTextBox.Text;
        string[] lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder(original.Length);
        bool changed = false;

        foreach (string rawLine in lines)
        {
            if (rawLine.Length == 0)
                continue;

            string line = rawLine;
            bool systemLine = IsSystemTerminalLine(line);
            if (!systemLine)
            {
                output.AppendLine(line);
                continue;
            }

            bool forcedRt950Scope = _specialToolLogScope == SpecialToolLogScope.Rt950 ||
                                    Volatile.Read(ref _rtx1TestInProgress) != 0 ||
                                    Volatile.Read(ref _rt950UnlockInProgress) != 0;
            if (forcedRt950Scope)
            {
                RouteSpecialSystemLine(SpecialToolLogScope.Rt950, line);
                changed = true;
                continue;
            }

            string message = ExtractSystemMessage(line);
            if (message.StartsWith("KISS RX", StringComparison.OrdinalIgnoreCase))
            {
                _kissRawBlockLinesRemaining = 5;
                RouteSpecialSystemLine(SpecialToolLogScope.Kiss, line);
                changed = true;
                continue;
            }
            if (_kissRawBlockLinesRemaining > 0)
            {
                _kissRawBlockLinesRemaining--;
                RouteSpecialSystemLine(SpecialToolLogScope.Kiss, line);
                changed = true;
                continue;
            }
            if (message.StartsWith("KISS WARNING=", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("KISS STATE ", StringComparison.OrdinalIgnoreCase))
            {
                RouteSpecialSystemLine(SpecialToolLogScope.Kiss, line);
                changed = true;
                continue;
            }

            // Auto-detect may internally recognize a device family. The generic main window
            // must not promote that recognition into a device/protocol-specific user mode.
            if (line.Contains("RADTEL_RT950_KISS", StringComparison.OrdinalIgnoreCase))
            {
                line = ReplaceIgnoreCase(line, "RADTEL_RT950_KISS", "Generic BLE-UART (FFE0/FFE1)");
                changed = true;
            }

            if (line.Contains("STATE RT950_KISS=", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("RADTEL KISS READY", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("RADTEL KISS NOT READY", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("RT950 LEGACY MANUAL COMMANDS", StringComparison.OrdinalIgnoreCase))
            {
                RouteSpecialSystemLine(SpecialToolLogScope.Rt950, line);
                changed = true;
                continue;
            }

            output.AppendLine(line);
        }

        if (!changed)
            return;

        _genericTerminalSanitizing = true;
        try
        {
            TerminalTextBox.Text = output.ToString();
            if (AutoScrollCheckBox.IsChecked == true)
                TerminalTextBox.ScrollToEnd();
        }
        finally
        {
            _genericTerminalSanitizing = false;
        }
    }

    private static bool IsSystemTerminalLine(string line)
    {
        int marker = line.IndexOf("*** ", StringComparison.Ordinal);
        return marker == 0 || (marker > 0 && line[0] == '[');
    }

    private static string ExtractSystemMessage(string line)
    {
        int marker = line.IndexOf("*** ", StringComparison.Ordinal);
        return marker >= 0 ? line[(marker + 4)..] : line;
    }

    private void RouteSpecialSystemLine(SpecialToolLogScope scope, string line)
    {
        string message = ExtractSystemMessage(line);
        if (scope == SpecialToolLogScope.Kiss)
            KissToolActivity?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        else
            Rt950ToolActivity?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    private static string ReplaceIgnoreCase(string source, string oldValue, string newValue)
    {
        int start = 0;
        var result = new StringBuilder(source.Length);
        while (true)
        {
            int index = source.IndexOf(oldValue, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                result.Append(source, start, source.Length - start);
                return result.ToString();
            }
            result.Append(source, start, index - start);
            result.Append(newValue);
            start = index + oldValue.Length;
        }
    }
}
