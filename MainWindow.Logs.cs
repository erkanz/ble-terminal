using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private readonly LogStore _structuredLogStore = AppLogHub.Store;
    private LogFilterWindow? _logFilterWindow;
    private int _terminalParsedLength;
    private readonly StringBuilder _terminalPendingLine = new();
    private int _rawTerminalMetadataLinesToSkip;
    private LogCategory? _terminalProtocolContext;
    private int _terminalProtocolContextRemaining;
    private int _txCaptureInProgress;

    private void InitializeLogExplorerHooks()
    {
        _terminalParsedLength = TerminalTextBox.Text.Length;
        TerminalTextBox.TextChanged += TerminalTextBox_StructuredLogTextChanged;
        NotificationCaptureHub.Store.RecordAdded += NotificationCaptureStore_RecordAddedForStructuredLog;
        SendButton.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(LogSendButton_Click), true);
        TxTextBox.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(LogTxTextBox_PreviewKeyDown), true);
    }

    private void ShutdownLogExplorerHooks()
    {
        TerminalTextBox.TextChanged -= TerminalTextBox_StructuredLogTextChanged;
        NotificationCaptureHub.Store.RecordAdded -= NotificationCaptureStore_RecordAddedForStructuredLog;
        SendButton.RemoveHandler(ButtonBase.ClickEvent, new RoutedEventHandler(LogSendButton_Click));
        TxTextBox.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(LogTxTextBox_PreviewKeyDown));
        CloseLogFilterWindow();
    }

    private void LogFilterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_logFilterWindow is { IsLoaded: true })
        {
            _logFilterWindow.Activate();
            return;
        }

        _logFilterWindow = new LogFilterWindow(_structuredLogStore, BuildLogSessionMetadata)
        {
            Owner = this
        };
        _logFilterWindow.Closed += (_, _) => _logFilterWindow = null;
        _logFilterWindow.Show();
    }

    private void CloseLogFilterWindow()
    {
        try { _logFilterWindow?.Close(); } catch { }
        _logFilterWindow = null;
    }

    private void TerminalTextBox_StructuredLogTextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            string text = TerminalTextBox.Text;
            if (text.Length < _terminalParsedLength)
            {
                _terminalParsedLength = text.Length;
                _terminalPendingLine.Clear();
                _rawTerminalMetadataLinesToSkip = 0;
                _terminalProtocolContext = null;
                _terminalProtocolContextRemaining = 0;
                return;
            }

            if (text.Length == _terminalParsedLength)
                return;

            _terminalPendingLine.Append(text.AsSpan(_terminalParsedLength));
            _terminalParsedLength = text.Length;

            while (TryTakeTerminalLine(out string line))
                CaptureStructuredTerminalLine(line);
        }
        catch
        {
            // Structured logging is diagnostic-only and must never affect the terminal path.
        }
    }

    private bool TryTakeTerminalLine(out string line)
    {
        string pending = _terminalPendingLine.ToString();
        int newline = pending.IndexOf('\n');
        if (newline < 0)
        {
            line = string.Empty;
            return false;
        }

        line = pending[..newline].TrimEnd('\r');
        _terminalPendingLine.Remove(0, newline + 1);
        return true;
    }

    private void CaptureStructuredTerminalLine(string line)
    {
        string content = StripTerminalTimestamp(line);
        if (!content.StartsWith("*** ", StringComparison.Ordinal))
            return;

        string message = content[4..];

        // Main RT-950 RX already enters the structured log from NotificationCaptureHub.
        // Suppress the four-line terminal rendering to avoid duplicate RX_RAW records.
        if (message.Equals("RAW BLE NOTIFICATION", StringComparison.OrdinalIgnoreCase))
        {
            _rawTerminalMetadataLinesToSkip = 3;
            return;
        }
        if (_rawTerminalMetadataLinesToSkip > 0)
        {
            _rawTerminalMetadataLinesToSkip--;
            return;
        }

        if (message.Equals("KISS RX", StringComparison.OrdinalIgnoreCase))
        {
            AddStructuredLog(LogCategory.KISS, message, characteristic: CurrentNotifyCharacteristicText());
            _terminalProtocolContext = LogCategory.KISS;
            _terminalProtocolContextRemaining = 5;
            return;
        }

        if (_terminalProtocolContextRemaining > 0 && _terminalProtocolContext.HasValue)
        {
            string characteristic = LogCategoryClassifier.InferCharacteristic(message);
            AddStructuredLog(_terminalProtocolContext.Value, message, characteristic: characteristic);
            _terminalProtocolContextRemaining--;
            if (_terminalProtocolContextRemaining == 0)
                _terminalProtocolContext = null;
            return;
        }

        (LogCategory category, bool warning, bool error) = LogCategoryClassifier.Classify(message);
        string direction = category == LogCategory.RX_RAW ? "RX" : category == LogCategory.TX_RAW ? "TX" : string.Empty;
        string inferredCharacteristic = LogCategoryClassifier.InferCharacteristic(message);
        AddStructuredLog(category, message, direction, inferredCharacteristic, warning, error);
    }

    private void NotificationCaptureStore_RecordAddedForStructuredLog(NotificationRecord record)
    {
        try
        {
            string message = $"BLE {record.DeliveryMode} len={record.Length} source={record.SourceText} hex={record.Hex} ascii={record.Ascii}";
            _structuredLogStore.Add(
                LogCategory.RX_RAW,
                message,
                direction: "RX",
                device: record.Device,
                characteristic: record.CharacteristicText,
                timestamp: record.Timestamp);
        }
        catch
        {
            // Never interfere with BLE notification processing.
        }
    }

    private void LogSendButton_Click(object sender, RoutedEventArgs e) => StartTxStructuredCapture();

    private void LogTxTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            StartTxStructuredCapture();
    }

    private void StartTxStructuredCapture()
    {
        if (Interlocked.Exchange(ref _txCaptureInProgress, 1) != 0)
            return;

        byte[] payload;
        try
        {
            if (_writeCharacteristic == null)
            {
                Interlocked.Exchange(ref _txCaptureInProgress, 0);
                return;
            }
            payload = BuildTxPayload();
            if (payload.Length == 0)
            {
                Interlocked.Exchange(ref _txCaptureInProgress, 0);
                return;
            }
        }
        catch
        {
            Interlocked.Exchange(ref _txCaptureInProgress, 0);
            return;
        }

        long txBefore = Interlocked.Read(ref _txBytes);
        string device = CurrentLogDevice();
        string characteristic = CurrentWriteCharacteristicText();
        _ = CaptureSuccessfulTxAsync(payload, txBefore, device, characteristic);
    }

    private async Task CaptureSuccessfulTxAsync(byte[] payload, long txBefore, string device, string characteristic)
    {
        try
        {
            long target = txBefore + payload.Length;
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(100);
                if (Interlocked.Read(ref _txBytes) >= target)
                {
                    string hex = BitConverter.ToString(payload).Replace('-', ' ');
                    string text = PrintableUtf8(payload);
                    _structuredLogStore.Add(
                        LogCategory.TX_RAW,
                        $"BLE WRITE len={payload.Length} hex={hex} text={text}",
                        direction: "TX",
                        device: device,
                        characteristic: characteristic);
                    return;
                }
                if (_writeCharacteristic == null)
                    return;
            }
        }
        catch
        {
            // Existing TX error handling remains authoritative.
        }
        finally
        {
            Interlocked.Exchange(ref _txCaptureInProgress, 0);
        }
    }

    private void AddStructuredLog(
        LogCategory category,
        string message,
        string direction = "",
        string characteristic = "",
        bool warning = false,
        bool error = false)
    {
        _structuredLogStore.Add(
            category,
            message,
            direction,
            CurrentLogDevice(),
            string.IsNullOrWhiteSpace(characteristic) ? LogCategoryClassifier.InferCharacteristic(message) : characteristic,
            warning,
            error);
    }

    private string CurrentLogDevice()
    {
        string name = _device?.Name?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        return _connectedAddress.HasValue ? FormatBluetoothAddress(_connectedAddress.Value) : "-";
    }

    private string CurrentNotifyCharacteristicText() =>
        _notifyCharacteristic == null ? string.Empty : BleUuid.Short(_notifyCharacteristic.Uuid);

    private string CurrentWriteCharacteristicText() =>
        _writeCharacteristic == null ? string.Empty : BleUuid.Short(_writeCharacteristic.Uuid);

    private string BuildLogSessionMetadata()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Connection: {ConnectionTextBlock.Text}");
        sb.AppendLine($"Device: {CurrentLogDevice()}");
        sb.AppendLine($"Address: {(_connectedAddress.HasValue ? FormatBluetoothAddress(_connectedAddress.Value) : "-")}");
        sb.AppendLine($"Connected at: {(_connectedAt.HasValue ? _connectedAt.Value.ToString("O") : "-")}");
        sb.AppendLine($"RX bytes: {Interlocked.Read(ref _rxBytes)}");
        sb.AppendLine($"TX bytes: {Interlocked.Read(ref _txBytes)}");
        if (_autoGatt.IsAvailable)
        {
            sb.AppendLine($"Profile: {_autoGatt.ProfileName}");
            sb.AppendLine($"Service: {BleUuid.Full(_autoGatt.Service!.Uuid)}");
            sb.AppendLine($"Write: {BleUuid.Full(_autoGatt.WriteCharacteristic!.Uuid)}");
            sb.AppendLine($"Notify: {BleUuid.Full(_autoGatt.NotifyCharacteristic!.Uuid)}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string StripTerminalTimestamp(string line)
    {
        if (!line.StartsWith("[", StringComparison.Ordinal))
            return line;
        int close = line.IndexOf("] ", StringComparison.Ordinal);
        return close >= 0 ? line[(close + 2)..] : line;
    }

    private static string PrintableUtf8(byte[] data)
    {
        string value = Encoding.UTF8.GetString(data);
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c == '\r') sb.Append("\\r");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\t') sb.Append("\\t");
            else if (char.IsControl(c)) sb.Append($"\\x{(int)c:X2}");
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
