using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private bool _usbSerialUiPrepared;
    private UsbSerialTransport? _usbSerial;
    private Expander? _bleDevicesExpander;
    private Expander? _usbSerialExpander;
    private ComboBox? _usbPortComboBox;
    private ComboBox? _usbBaudComboBox;
    private ComboBox? _usbTxModeComboBox;
    private ComboBox? _usbLineEndingComboBox;
    private TextBox? _usbRawTxTextBox;
    private TextBlock? _usbSerialStatusText;
    private Button? _usbSerialConnectButton;
    private Button? _usbSerialDisconnectButton;
    private Button? _usbSerialRawSendButton;
    private Button? _usbSerialRtx1Button;
    private TaskCompletionSource<byte[]>? _usbRtx1DiagnosticTcs;

    internal void PrepareUsbSerialUi()
    {
        if (_usbSerialUiPrepared)
            return;
        _usbSerialUiPrepared = true;

        _usbSerial = new UsbSerialTransport();
        _usbSerial.DataReceived += UsbSerial_DataReceived;
        _usbSerial.Faulted += UsbSerial_Faulted;
        Closing += (_, _) => ShutdownUsbSerial();

        BuildCollapsibleDeviceAndSerialUi();
        RefreshUsbSerialPorts();
        UpdateUsbSerialControls();
    }

    private void BuildCollapsibleDeviceAndSerialUi()
    {
        if (DeviceGrid.Parent is not Grid root)
            return;

        int row = Grid.GetRow(DeviceGrid);
        root.Children.Remove(DeviceGrid);
        if (row >= 0 && row < root.RowDefinitions.Count)
            root.RowDefinitions[row].Height = GridLength.Auto;

        DeviceGrid.Height = 150;
        DeviceGrid.Margin = new Thickness(0);

        var bleBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Child = DeviceGrid
        };
        bleBorder.SetResourceReference(Border.BackgroundProperty, "PanelBackgroundBrush");
        bleBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        _bleDevicesExpander = new Expander
        {
            IsExpanded = true,
            Margin = new Thickness(0, 0, 0, 8),
            Content = bleBorder
        };
        UpdateBleDevicesExpanderHeader();
        _visibleDevices.CollectionChanged += (_, _) =>
            _ = Dispatcher.BeginInvoke(UpdateBleDevicesExpanderHeader);

        _usbSerialExpander = new Expander
        {
            Header = "USB Serial / COM Port",
            IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 8),
            Content = BuildUsbSerialPanel()
        };

        var host = new StackPanel();
        host.Children.Add(_bleDevicesExpander);
        host.Children.Add(_usbSerialExpander);
        Grid.SetRow(host, row);
        root.Children.Add(host);
    }

    private void UpdateBleDevicesExpanderHeader()
    {
        if (_bleDevicesExpander != null)
            _bleDevicesExpander.Header = $"Scanned BLE Devices ({_visibleDevices.Count})";
    }

    private UIElement BuildUsbSerialPanel()
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8)
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBackgroundBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var stack = new StackPanel();
        border.Child = stack;

        var connection = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        connection.Children.Add(NewUsbLabel("Port:"));
        _usbPortComboBox = new ComboBox { Width = 110, Height = 28, Margin = new Thickness(0, 0, 6, 0) };
        _usbPortComboBox.SelectionChanged += (_, _) => UpdateUsbSerialControls();
        connection.Children.Add(_usbPortComboBox);

        var refresh = new Button { Content = "Refresh", Width = 78, Height = 28, Margin = new Thickness(0, 0, 10, 0) };
        refresh.Click += (_, _) => RefreshUsbSerialPorts();
        connection.Children.Add(refresh);

        connection.Children.Add(NewUsbLabel("Baud:"));
        _usbBaudComboBox = new ComboBox
        {
            Width = 105,
            Height = 28,
            Margin = new Thickness(0, 0, 10, 0),
            ItemsSource = new[] { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 },
            SelectedItem = 115200
        };
        connection.Children.Add(_usbBaudComboBox);

        _usbSerialConnectButton = new Button { Content = "Connect", Width = 86, Height = 28, Margin = new Thickness(0, 0, 6, 0) };
        _usbSerialConnectButton.Click += UsbSerialConnectButton_Click;
        connection.Children.Add(_usbSerialConnectButton);

        _usbSerialDisconnectButton = new Button { Content = "Disconnect", Width = 92, Height = 28, Margin = new Thickness(0, 0, 10, 0) };
        _usbSerialDisconnectButton.Click += UsbSerialDisconnectButton_Click;
        connection.Children.Add(_usbSerialDisconnectButton);

        _usbSerialStatusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _usbSerialStatusText.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundBrush");
        connection.Children.Add(_usbSerialStatusText);
        stack.Children.Add(connection);

        var raw = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        raw.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        raw.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        raw.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        raw.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        raw.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        raw.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var rawLabel = NewUsbLabel("Raw TX:");
        Grid.SetColumn(rawLabel, 0);
        raw.Children.Add(rawLabel);

        _usbRawTxTextBox = new TextBox
        {
            Height = 28,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(5, 2, 5, 2),
            FontFamily = new System.Windows.Media.FontFamily("Consolas")
        };
        _usbRawTxTextBox.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter)
                return;
            e.Handled = true;
            await SendUsbSerialRawAsync();
        };
        Grid.SetColumn(_usbRawTxTextBox, 1);
        raw.Children.Add(_usbRawTxTextBox);

        _usbTxModeComboBox = new ComboBox
        {
            Width = 72,
            Height = 28,
            Margin = new Thickness(0, 0, 6, 0),
            ItemsSource = new[] { "ASCII", "HEX" },
            SelectedIndex = 0
        };
        Grid.SetColumn(_usbTxModeComboBox, 2);
        raw.Children.Add(_usbTxModeComboBox);

        _usbLineEndingComboBox = new ComboBox
        {
            Width = 76,
            Height = 28,
            Margin = new Thickness(0, 0, 6, 0),
            ItemsSource = new[] { "None", "CR", "LF", "CRLF" },
            SelectedIndex = 0
        };
        Grid.SetColumn(_usbLineEndingComboBox, 3);
        raw.Children.Add(_usbLineEndingComboBox);

        _usbSerialRawSendButton = new Button { Content = "Send", Width = 70, Height = 28, Margin = new Thickness(0, 0, 10, 0) };
        _usbSerialRawSendButton.Click += async (_, _) => await SendUsbSerialRawAsync();
        Grid.SetColumn(_usbSerialRawSendButton, 4);
        raw.Children.Add(_usbSerialRawSendButton);

        _usbSerialRtx1Button = new Button { Content = "Run RTX1 TX Test over USB", MinWidth = 190, Height = 28 };
        _usbSerialRtx1Button.Click += async (_, _) => await RunRt950Rtx1UsbTestAsync(1);
        Grid.SetColumn(_usbSerialRtx1Button, 5);
        raw.Children.Add(_usbSerialRtx1Button);
        stack.Children.Add(raw);

        var note = new TextBlock
        {
            Text = "USB RTX1 sends the verified RTX1 binary packet directly to the COM stream. BLE-only FF31/FFE1 GATT handshake routing is not used on USB.",
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundBrush");
        stack.Children.Add(note);

        return border;
    }

    private static TextBlock NewUsbLabel(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0)
    };

    private void RefreshUsbSerialPorts()
    {
        if (_usbPortComboBox == null)
            return;

        string previous = _usbPortComboBox.SelectedItem?.ToString() ?? string.Empty;
        try
        {
            IReadOnlyList<string> ports = UsbSerialTransport.GetPortNames();
            _usbPortComboBox.ItemsSource = ports;
            _usbPortComboBox.SelectedItem = ports.FirstOrDefault(p => string.Equals(p, previous, StringComparison.OrdinalIgnoreCase)) ?? ports.FirstOrDefault();
            if (ports.Count == 0 && _usbSerialStatusText != null)
                _usbSerialStatusText.Text = "No COM ports found";
        }
        catch (Exception ex)
        {
            AppendSystemLine($"USB SERIAL PORT ENUMERATION ERROR: {ex.Message}");
            if (_usbSerialStatusText != null)
                _usbSerialStatusText.Text = "Port enumeration failed";
        }
        UpdateUsbSerialControls();
    }

    private void UsbSerialConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_usbSerial == null || _usbPortComboBox?.SelectedItem is not string portName ||
            _usbBaudComboBox?.SelectedItem is not int baudRate)
            return;

        try
        {
            _usbSerial.Open(portName, baudRate);
            AppendSystemLine("USB SERIAL CONNECTED");
            AppendSystemLine($"PORT={portName}");
            AppendSystemLine($"BAUD={baudRate}");
            SetStatus($"USB serial connected: {portName} @ {baudRate}");
        }
        catch (Exception ex)
        {
            AppendSystemLine("USB SERIAL CONNECT FAILED");
            AppendSystemLine($"PORT={portName}");
            AppendSystemLine($"ERROR={ex.Message}");
            MessageBox.Show(this, ex.Message, "USB serial connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateUsbSerialControls();
    }

    private void UsbSerialDisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        string port = _usbSerial?.PortName ?? string.Empty;
        _usbSerial?.Close();
        _usbRtx1DiagnosticTcs?.TrySetCanceled();
        _usbRtx1DiagnosticTcs = null;
        AppendSystemLine($"USB SERIAL DISCONNECTED{(string.IsNullOrWhiteSpace(port) ? string.Empty : $" port={port}")}");
        SetStatus("USB serial disconnected");
        UpdateUsbSerialControls();
    }

    private void UsbSerial_DataReceived(byte[] data)
    {
        if (data.Length == 0)
            return;

        _usbRtx1DiagnosticTcs?.TrySetResult(data.ToArray());
        Interlocked.Add(ref _rxBytes, data.Length);
        _ = Dispatcher.BeginInvoke(() =>
        {
            AppendSystemLine("RAW USB SERIAL RX");
            AppendSystemLine($"PORT={_usbSerial?.PortName ?? "-"}");
            AppendSystemLine($"LEN={data.Length}");
            AppendSystemLine($"HEX={Hex(data)}");
            AppendRx(data);
            UpdateCounters();
        });
    }

    private void UsbSerial_Faulted(string message)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            AppendSystemLine($"USB SERIAL ERROR: {message}");
            UpdateUsbSerialControls();
        });
    }

    private async Task SendUsbSerialRawAsync()
    {
        if (_usbSerial?.IsOpen != true || _usbRawTxTextBox == null)
        {
            MessageBox.Show(this, "Connect a USB serial COM port first.", "USB Serial", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool hexMode = string.Equals(_usbTxModeComboBox?.SelectedItem?.ToString(), "HEX", StringComparison.OrdinalIgnoreCase);
        TxLineEnding ending = (_usbLineEndingComboBox?.SelectedItem?.ToString() ?? "None") switch
        {
            "CR" => TxLineEnding.Cr,
            "LF" => TxLineEnding.Lf,
            "CRLF" => TxLineEnding.CrLf,
            _ => TxLineEnding.None
        };

        byte[] payload;
        try
        {
            payload = TxPayloadBuilder.Build(_usbRawTxTextBox.Text, hexMode, ending);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid USB serial TX data", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (payload.Length == 0)
            return;

        try
        {
            AppendSystemLine("RAW USB SERIAL TX");
            AppendSystemLine($"PORT={_usbSerial.PortName}");
            AppendSystemLine($"LEN={payload.Length}");
            AppendSystemLine($"HEX={Hex(payload)}");
            await _usbSerial.WriteAsync(payload);
            Interlocked.Add(ref _txBytes, payload.Length);
            if (LocalEchoCheckBox.IsChecked == true)
                AppendTx(payload);
            UpdateCounters();
            _usbRawTxTextBox.Clear();
        }
        catch (Exception ex)
        {
            AppendSystemLine($"USB SERIAL TX ERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, "USB serial write failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal async Task RunRt950Rtx1UsbTestAsync(int count)
    {
        if (count is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(count), "RTX1 repeat count must be 1 through 99.");

        if (_usbSerial?.IsOpen != true)
        {
            AppendSystemLine("*** RTX1 USB TEST REJECTED");
            AppendSystemLine("*** REASON=USB_SERIAL_NOT_CONNECTED");
            return;
        }

        if (Interlocked.CompareExchange(ref _rtx1TestInProgress, 1, 0) != 0)
        {
            AppendSystemLine("*** RTX1 USB TEST REJECTED");
            AppendSystemLine("*** REASON=TEST_ALREADY_IN_PROGRESS");
            return;
        }

        if (_usbSerialRtx1Button != null)
            _usbSerialRtx1Button.IsEnabled = false;
        if (_runRt950Rtx1TestButton != null)
            _runRt950Rtx1TestButton.IsEnabled = false;

        try
        {
            Task queued = _manualTxQueue.Enqueue(() =>
                Dispatcher.InvokeAsync(() => ExecuteUsbRtx1SequenceAsync(count)).Task.Unwrap());
            await queued;
        }
        catch (Exception ex)
        {
            AppendSystemLine("*** RTX1 AUTO TEST COMPLETE");
            AppendSystemLine("*** TRANSPORT=USB_SERIAL");
            AppendSystemLine($"*** INTERNAL_ERROR={ex.Message}");
            AppendSystemLine("*** USB_SERIAL_TRANSPORT=FAIL");
            AppendSystemLine("*** RTX1_DEVICE_ACK=NOT_SEEN");
            AppendSystemLine("*** RF_TX=UNKNOWN");
        }
        finally
        {
            _usbRtx1DiagnosticTcs?.TrySetCanceled();
            _usbRtx1DiagnosticTcs = null;
            Interlocked.Exchange(ref _rtx1TestInProgress, 0);
            if (_runRt950Rtx1TestButton != null)
                _runRt950Rtx1TestButton.IsEnabled = true;
            UpdateUsbSerialControls();
        }
    }

    private async Task ExecuteUsbRtx1SequenceAsync(int count)
    {
        for (int i = 0; i < count; i++)
        {
            int counter = Interlocked.Increment(ref _rtx1FrameCounter);
            if (counter > 9999)
            {
                Interlocked.Exchange(ref _rtx1FrameCounter, 1);
                counter = 1;
            }

            if (!await ExecuteUsbRtx1FrameAsync(counter))
                break;
            if (i + 1 < count)
                await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private async Task<bool> ExecuteUsbRtx1FrameAsync(int counter)
    {
        if (_usbSerial?.IsOpen != true)
        {
            LogUsbRtx1Final(fcsPass: false, writePass: false, diagnosticSeen: false, reason: "USB_SERIAL_NOT_CONNECTED");
            return false;
        }

        string information = Rt950Rtx1Protocol.InformationText(counter);
        LogRt950Rtx1Start(information);
        AppendSystemLine("*** TRANSPORT=USB_SERIAL");
        AppendSystemLine($"*** USB_PORT={_usbSerial.PortName}");
        AppendSystemLine($"*** USB_BAUD={_usbSerial.BaudRate}");
        AppendSystemLine("*** BLE_HANDSHAKE=NOT_APPLICABLE");

        byte[] packet;
        byte[] ax25Raw;
        ushort fcs;
        try
        {
            packet = Rt950Rtx1Protocol.BuildRtx1Packet(counter, out ax25Raw, out fcs);
        }
        catch (Exception ex)
        {
            AppendSystemLine($"*** AX25_FCS_VERIFY=FAIL ({ex.Message})");
            AppendSystemLine("*** RTX1_SEND=SKIPPED");
            LogUsbRtx1Final(fcsPass: false, writePass: false, diagnosticSeen: false, reason: "FCS_SELF_VERIFY_FAILED");
            return false;
        }

        byte[] wireFcs = Rt950Rtx1Protocol.FcsWireBytes(fcs);
        bool fcsPass = Rt950Rtx1Protocol.VerifyFcs(ax25Raw, wireFcs);
        AppendSystemLine($"*** AX25_RAW_LEN={ax25Raw.Length}");
        AppendSystemLine($"*** AX25_RAW_HEX={Hex(ax25Raw)}");
        AppendSystemLine($"*** AX25_FCS_CALC=0x{fcs:X4}");
        AppendSystemLine($"*** AX25_FCS_WIRE={Hex(wireFcs)}");
        AppendSystemLine($"*** AX25_FCS_VERIFY={(fcsPass ? "PASS" : "FAIL")}");
        if (!fcsPass)
        {
            AppendSystemLine("*** RTX1_SEND=SKIPPED");
            LogUsbRtx1Final(fcsPass: false, writePass: false, diagnosticSeen: false, reason: "FCS_SELF_VERIFY_FAILED");
            return false;
        }

        AppendSystemLine("*** RTX1 USB SERIAL PACKET");
        AppendSystemLine($"*** LEN={packet.Length}");
        AppendSystemLine($"*** HEX={Hex(packet)}");
        AppendSystemLine("*** WRITE_MODE=RAW_BINARY_STREAM");

        var diagnosticTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _usbRtx1DiagnosticTcs = diagnosticTcs;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _usbSerial.WriteAsync(packet);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppendSystemLine("*** RTX1_SERIAL_WRITE=FAIL");
            AppendSystemLine($"*** ERROR={ex.Message}");
            LogUsbRtx1Final(fcsPass: true, writePass: false, diagnosticSeen: false, reason: "SERIAL_WRITE_FAILED");
            return false;
        }
        stopwatch.Stop();

        Interlocked.Add(ref _txBytes, packet.Length);
        UpdateCounters();
        AppendSystemLine("*** RTX1_SERIAL_WRITE=PASS");
        AppendSystemLine($"*** RTX1_SERIAL_WRITE_LEN={packet.Length}");
        AppendSystemLine($"*** RTX1_SERIAL_WRITE_DURATION_MS={stopwatch.Elapsed.TotalMilliseconds:F1}");

        Task diagnosticCompleted = await Task.WhenAny(diagnosticTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        bool diagnosticSeen = diagnosticCompleted == diagnosticTcs.Task &&
            !diagnosticTcs.Task.IsCanceled && !diagnosticTcs.Task.IsFaulted;

        if (diagnosticSeen)
        {
            byte[] response = await diagnosticTcs.Task;
            AppendSystemLine("*** RTX1 USB SERIAL DIAGNOSTIC RX");
            AppendSystemLine($"*** LEN={response.Length}");
            AppendSystemLine($"*** HEX={Hex(response)}");
            AppendSystemLine("*** CLASSIFICATION=UNPARSED");
        }

        LogUsbRtx1Final(fcsPass: true, writePass: true, diagnosticSeen: diagnosticSeen, reason: null);
        return true;
    }

    private void LogUsbRtx1Final(bool fcsPass, bool writePass, bool diagnosticSeen, string? reason)
    {
        AppendSystemLine("*** RTX1 AUTO TEST COMPLETE");
        AppendSystemLine("*** TRANSPORT=USB_SERIAL");
        AppendSystemLine("*** BLE_HANDSHAKE=NOT_APPLICABLE");
        AppendSystemLine($"*** AX25_FCS={(fcsPass ? "PASS" : "NOT_COMPLETED")}");
        AppendSystemLine("*** RTX1_ALL_CHUNKS=NOT_APPLICABLE");
        AppendSystemLine($"*** USB_SERIAL_TRANSPORT={(writePass ? "PASS" : "FAIL")}");
        AppendSystemLine($"*** RTX1_DIAGNOSTIC_SERIAL_RX={(diagnosticSeen ? "SEEN_UNPARSED" : "NOT_SEEN")}");
        AppendSystemLine("*** RTX1_DEVICE_ACK=NOT_SEEN");
        AppendSystemLine("*** RF_TX=UNKNOWN");
        if (!string.IsNullOrWhiteSpace(reason))
            AppendSystemLine($"*** REASON={reason}");
    }

    private void UpdateUsbSerialControls()
    {
        bool connected = _usbSerial?.IsOpen == true;
        bool hasPort = _usbPortComboBox?.SelectedItem is string;
        if (_usbPortComboBox != null) _usbPortComboBox.IsEnabled = !connected;
        if (_usbBaudComboBox != null) _usbBaudComboBox.IsEnabled = !connected;
        if (_usbSerialConnectButton != null) _usbSerialConnectButton.IsEnabled = !connected && hasPort;
        if (_usbSerialDisconnectButton != null) _usbSerialDisconnectButton.IsEnabled = connected;
        if (_usbSerialRawSendButton != null) _usbSerialRawSendButton.IsEnabled = connected;
        if (_usbSerialRtx1Button != null) _usbSerialRtx1Button.IsEnabled = connected && Volatile.Read(ref _rtx1TestInProgress) == 0;
        if (_usbSerialStatusText != null)
            _usbSerialStatusText.Text = connected
                ? $"Connected: {_usbSerial!.PortName} @ {_usbSerial.BaudRate}"
                : (_usbPortComboBox?.Items.Count > 0 ? "Disconnected" : "No COM ports found");
        if (_usbSerialExpander != null && connected)
            _usbSerialExpander.Header = $"USB Serial / COM Port — {_usbSerial!.PortName} CONNECTED";
        else if (_usbSerialExpander != null)
            _usbSerialExpander.Header = "USB Serial / COM Port";
    }

    private void ShutdownUsbSerial()
    {
        if (_usbSerial == null)
            return;
        _usbSerial.DataReceived -= UsbSerial_DataReceived;
        _usbSerial.Faulted -= UsbSerial_Faulted;
        _usbSerial.Dispose();
        _usbSerial = null;
    }
}
