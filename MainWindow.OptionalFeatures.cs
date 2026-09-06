using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private OptionalFeatureSettings _optionalFeatureSettings = new();
    private MenuItem? _rt950Menu;
    private MenuItem? _rt950EnableMenuItem;
    private MenuItem? _rt950PresetMenuItem;
    private MenuItem? _rt950UnlockMenuItem;
    private MenuItem? _rt950AutoUnlockMenuItem;
    private MenuItem? _rt950LoadCommandsMenuItem;
    private MenuItem? _rt950DiagnosticsMenuItem;
    private MenuItem? _kissMenu;
    private MenuItem? _kissEnableMenuItem;
    private MenuItem? _kissDecoderMenuItem;
    private MenuItem? _kissRawMenuItem;
    private MenuItem? _kissDecodedMenuItem;
    private MenuItem? _kissBuilderMenuItem;
    private MenuItem? _kissClearMenuItem;
    private Rt950UnlockState _rt950UnlockState = Rt950UnlockState.Disconnected;
    private bool _rt950Unlocked;
    private int _rt950UnlockInProgress;
    private TaskCompletionSource<byte[]>? _rt950UnlockResponseTcs;

    private static string OptionalFeatureSettingsFile => System.IO.Path.Combine(SettingsDirectory, "optional-features.json");

    private bool IsRt950DataPathReady =>
        _optionalFeatureSettings.Rt950ToolsEnabled && _rt950Unlocked && _rt950UnlockState == Rt950UnlockState.Ready;

    private bool IsKissRxProcessingEnabled =>
        _optionalFeatureSettings.KissToolsEnabled && _optionalFeatureSettings.KissRxDecoderEnabled;

    private bool IsKissDecodedDisplayEnabled =>
        IsKissRxProcessingEnabled && _optionalFeatureSettings.KissShowDecoded;

    private void InitializeOptionalFeatureModules()
    {
        LoadOptionalFeatureSettings();
        BuildOptionalFeatureMenus();
        GattInspectorButton.IsEnabledChanged += OptionalFeatureConnectionStateChanged;
        NotificationCaptureHub.Store.RecordAdded += OptionalFeatureNotification_RecordAdded;
        KissStreamDecoder.ProcessingPolicy = ShouldProcessKissDecoder;
        ResetRt950FeatureSession();
        UpdateOptionalFeatureMenuState();
    }

    private void ShutdownOptionalFeatureModules()
    {
        GattInspectorButton.IsEnabledChanged -= OptionalFeatureConnectionStateChanged;
        NotificationCaptureHub.Store.RecordAdded -= OptionalFeatureNotification_RecordAdded;
        _rt950UnlockResponseTcs?.TrySetCanceled();
        _rt950UnlockResponseTcs = null;
        if (KissStreamDecoder.ProcessingPolicy == ShouldProcessKissDecoder)
            KissStreamDecoder.ProcessingPolicy = null;
        SaveOptionalFeatureSettings();
    }

    private bool ShouldProcessKissDecoder(KissStreamDecoder decoder)
    {
        // The former RT950-embedded parser is deliberately retired. The selected main RX
        // stream is decoded only by the independent KISS feature decoder when enabled.
        if (ReferenceEquals(decoder, _autoKissDecoder))
            return false;
        if (ReferenceEquals(decoder, _notificationMonitorKissDecoder))
            return IsKissRxProcessingEnabled;
        return true;
    }

    private void BuildOptionalFeatureMenus()
    {
        if (Content is not DockPanel dock)
            return;
        Menu? menu = dock.Children.OfType<Menu>().FirstOrDefault();
        if (menu == null)
            return;

        _rt950Menu = new MenuItem { Header = "_RT950" };
        _rt950EnableMenuItem = NewCheckMenu("Enable RT950 Tools", _optionalFeatureSettings.Rt950ToolsEnabled, Rt950EnableMenuItem_Click);
        _rt950PresetMenuItem = NewActionMenu("Apply RT950 GATT Preset", async (_, _) => await ApplyRt950GattPresetAsync());
        _rt950UnlockMenuItem = NewActionMenu("Unlock RT950 BLE", async (_, _) => await QueueRt950UnlockAsync("MANUAL_MENU"));
        _rt950AutoUnlockMenuItem = NewCheckMenu("Auto Unlock On Connect", _optionalFeatureSettings.Rt950AutoUnlockOnConnect, Rt950AutoUnlockMenuItem_Click);
        _rt950LoadCommandsMenuItem = NewActionMenu("Load RT950 Test Commands", (_, _) => AddRt950PresetCommands());
        _rt950DiagnosticsMenuItem = NewActionMenu("RT950 Diagnostics", (_, _) => ShowRt950Diagnostics());
        _rt950Menu.Items.Add(_rt950EnableMenuItem);
        _rt950Menu.Items.Add(new Separator());
        _rt950Menu.Items.Add(_rt950PresetMenuItem);
        _rt950Menu.Items.Add(_rt950UnlockMenuItem);
        _rt950Menu.Items.Add(_rt950AutoUnlockMenuItem);
        _rt950Menu.Items.Add(_rt950LoadCommandsMenuItem);
        _rt950Menu.Items.Add(new Separator());
        _rt950Menu.Items.Add(_rt950DiagnosticsMenuItem);

        _kissMenu = new MenuItem { Header = "_KISS" };
        _kissEnableMenuItem = NewCheckMenu("Enable KISS Tools", _optionalFeatureSettings.KissToolsEnabled, KissEnableMenuItem_Click);
        _kissDecoderMenuItem = NewCheckMenu("Enable KISS RX Decoder", _optionalFeatureSettings.KissRxDecoderEnabled, KissDecoderMenuItem_Click);
        _kissRawMenuItem = NewCheckMenu("Show Raw KISS Frames", _optionalFeatureSettings.KissShowRawFrames, KissRawMenuItem_Click);
        _kissDecodedMenuItem = NewCheckMenu("Show Decoded AX.25/APRS", _optionalFeatureSettings.KissShowDecoded, KissDecodedMenuItem_Click);
        _kissBuilderMenuItem = NewActionMenu("KISS Frame Builder", (_, _) => ShowKissFrameBuilder());
        _kissClearMenuItem = NewActionMenu("Clear KISS State", (_, _) => ClearKissFeatureState());
        _kissMenu.Items.Add(_kissEnableMenuItem);
        _kissMenu.Items.Add(new Separator());
        _kissMenu.Items.Add(_kissDecoderMenuItem);
        _kissMenu.Items.Add(_kissRawMenuItem);
        _kissMenu.Items.Add(_kissDecodedMenuItem);
        _kissMenu.Items.Add(new Separator());
        _kissMenu.Items.Add(_kissBuilderMenuItem);
        _kissMenu.Items.Add(_kissClearMenuItem);

        MenuItem? settings = menu.Items.OfType<MenuItem>().FirstOrDefault(x => HeaderText(x).Equals("Settings", StringComparison.OrdinalIgnoreCase));
        int insertIndex = settings != null ? menu.Items.IndexOf(settings) : Math.Max(0, menu.Items.Count - 1);
        menu.Items.Insert(insertIndex, _rt950Menu);
        menu.Items.Insert(insertIndex + 1, _kissMenu);

        if (settings != null)
        {
            MenuItem? oldFixedRows = settings.Items.OfType<MenuItem>()
                .FirstOrDefault(x => HeaderText(x).Contains("Number of TX command rows", StringComparison.OrdinalIgnoreCase));
            if (oldFixedRows != null)
                settings.Items.Remove(oldFixedRows);

            var confirmRemove = NewCheckMenu("Confirm command removal", CommandRemoveConfirmationEnabled, (_, _) =>
            {
                if (confirmRemovePlaceholder is MenuItem item)
                    SetCommandRemoveConfirmation(item.IsChecked);
            });
            // Local indirection avoids depending on a fixed XAML name; the lambda below is replaced immediately.
            settings.Items.Add(new Separator());
            settings.Items.Add(confirmRemove);
            confirmRemove.Click -= NoopMenuClick;
            confirmRemove.Click += (_, _) => SetCommandRemoveConfirmation(confirmRemove.IsChecked);
        }
    }

    // Kept solely so BuildOptionalFeatureMenus can create handlers without introducing XAML names.
    private static readonly object? confirmRemovePlaceholder = null;
    private static void NoopMenuClick(object sender, RoutedEventArgs e) { }

    private static MenuItem NewCheckMenu(string header, bool isChecked, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += click;
        return item;
    }

    private static MenuItem NewActionMenu(string header, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header };
        item.Click += click;
        return item;
    }

    private static string HeaderText(MenuItem item) => (item.Header?.ToString() ?? string.Empty).Replace("_", string.Empty);

    private async void Rt950EnableMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _optionalFeatureSettings.Rt950ToolsEnabled = _rt950EnableMenuItem?.IsChecked == true;
        if (!_optionalFeatureSettings.Rt950ToolsEnabled)
        {
            _rt950UnlockResponseTcs?.TrySetCanceled();
            ResetRt950FeatureSession();
            AppendSystemLine("RT950 TOOLS DISABLED; generic BLE terminal remains active");
        }
        else
        {
            AppendSystemLine("RT950 TOOLS ENABLED");
            if (_bleConnected)
            {
                _rt950UnlockState = _servicesDiscovered ? Rt950UnlockState.ServicesDiscovered : Rt950UnlockState.Connected;
                if (_ffe1CccdEnabled && _notifyCharacteristic != null && BleUuid.Is(_notifyCharacteristic.Uuid, "FFE1"))
                    _rt950UnlockState = Rt950UnlockState.Ffe1NotifyReady;
                if (_optionalFeatureSettings.Rt950AutoUnlockOnConnect)
                    await QueueRt950UnlockAsync("TOOLS_ENABLED_WHILE_CONNECTED");
            }
        }
        SaveOptionalFeatureSettings();
        UpdateOptionalFeatureMenuState();
    }

    private async void Rt950AutoUnlockMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _optionalFeatureSettings.Rt950AutoUnlockOnConnect = _rt950AutoUnlockMenuItem?.IsChecked == true;
        SaveOptionalFeatureSettings();
        if (_optionalFeatureSettings.Rt950ToolsEnabled && _optionalFeatureSettings.Rt950AutoUnlockOnConnect && _bleConnected)
            await QueueRt950UnlockAsync("AUTO_UNLOCK_ENABLED");
    }

    private void KissEnableMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _optionalFeatureSettings.KissToolsEnabled = _kissEnableMenuItem?.IsChecked == true;
        ClearKissFeatureState(log: false);
        AppendSystemLine(_optionalFeatureSettings.KissToolsEnabled
            ? "KISS TOOLS ENABLED; raw BLE stream remains visible"
            : "KISS TOOLS DISABLED; BLE RX is raw terminal data only");
        SaveOptionalFeatureSettings();
        UpdateOptionalFeatureMenuState();
    }

    private void KissDecoderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _optionalFeatureSettings.KissRxDecoderEnabled = _kissDecoderMenuItem?.IsChecked == true;
        ClearKissFeatureState(log: false);
        SaveOptionalFeatureSettings();
        AppendSystemLine($"KISS RX DECODER={(_optionalFeatureSettings.KissRxDecoderEnabled ? "ON" : "OFF")}");
    }

    private void KissRawMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _optionalFeatureSettings.KissShowRawFrames = _kissRawMenuItem?.IsChecked == true;
        SaveOptionalFeatureSettings();
    }

    private void KissDecodedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _optionalFeatureSettings.KissShowDecoded = _kissDecodedMenuItem?.IsChecked == true;
        SaveOptionalFeatureSettings();
        if (IsKissDecodedDisplayEnabled)
            DecodedPacketsMenuItem_Click(sender, e);
    }

    private async void OptionalFeatureConnectionStateChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!GattInspectorButton.IsEnabled || !_bleConnected || _service == null)
        {
            ResetRt950FeatureSession();
            UpdateOptionalFeatureMenuState();
            return;
        }

        _rt950UnlockState = Rt950UnlockState.Connected;
        if (_servicesDiscovered)
            _rt950UnlockState = Rt950UnlockState.ServicesDiscovered;
        if (_notifyCharacteristic != null && BleUuid.Is(_notifyCharacteristic.Uuid, "FFE1"))
            _rt950UnlockState = _ffe1CccdEnabled ? Rt950UnlockState.Ffe1NotifyReady : Rt950UnlockState.Ffe1NotifyEnabling;

        if (!_optionalFeatureSettings.Rt950ToolsEnabled)
        {
            // Do not let auto-detected RT950 metadata turn the default application into an RT950 mode.
            _radtelKissReady = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_bleConnected && !_optionalFeatureSettings.Rt950ToolsEnabled)
                    SetStatus("BLE UART ready (generic terminal mode)");
            });
        }
        else if (_optionalFeatureSettings.Rt950AutoUnlockOnConnect)
        {
            await QueueRt950UnlockAsync("AUTO_CONNECT");
        }

        UpdateOptionalFeatureMenuState();
    }

    private void ResetRt950FeatureSession()
    {
        _rt950UnlockResponseTcs?.TrySetCanceled();
        _rt950UnlockResponseTcs = null;
        _rt950Unlocked = false;
        _rt950UnlockState = _bleConnected ? Rt950UnlockState.Connected : Rt950UnlockState.Disconnected;
        Interlocked.Exchange(ref _rt950UnlockInProgress, 0);
    }

    private async Task QueueRt950UnlockAsync(string source)
    {
        if (!_optionalFeatureSettings.Rt950ToolsEnabled)
        {
            AppendSystemLine("RT950 UNLOCK FAILED");
            AppendSystemLine("REASON=RT950_TOOLS_DISABLED");
            return;
        }
        if (IsRt950DataPathReady)
        {
            AppendSystemLine("RT950 BLE DATA PATH READY");
            return;
        }
        if (Interlocked.CompareExchange(ref _rt950UnlockInProgress, 1, 0) != 0)
        {
            AppendSystemLine("RT950 UNLOCK ALREADY IN PROGRESS");
            return;
        }

        Task queued = _manualTxQueue.Enqueue(() =>
            Dispatcher.InvokeAsync(() => ExecuteRt950UnlockCoreAsync(source), DispatcherPriority.Normal).Task.Unwrap());
        try
        {
            await queued;
        }
        finally
        {
            Interlocked.Exchange(ref _rt950UnlockInProgress, 0);
        }
    }

    private async Task ExecuteRt950UnlockCoreAsync(string source)
    {
        if (!TryResolveRt950Transport(requireRecognizedIdentity: source.StartsWith("AUTO", StringComparison.OrdinalIgnoreCase),
                out GattCharacteristic? dataCharacteristic,
                out GattCharacteristic? unlockCharacteristic,
                out string reason))
        {
            MarkRt950UnlockFailed(reason);
            return;
        }

        if (!_ffe1CccdEnabled || _notifyCharacteristic == null || !BleUuid.Is(_notifyCharacteristic.Uuid, "FFE1"))
        {
            MarkRt950UnlockFailed("FFE1_NOTIFY_NOT_READY");
            return;
        }

        _rt950UnlockState = Rt950UnlockState.Ffe1NotifyReady;
        if (source.StartsWith("AUTO", StringComparison.OrdinalIgnoreCase))
            AppendSystemLine("RT950 AUTO UNLOCK START");
        AppendSystemLine("RT950 BLE UNLOCK");
        AppendSystemLine("SERVICE=FFE0");
        AppendSystemLine("UUID=FF31");
        AppendSystemLine("TYPE=WITH_RESPONSE");
        AppendSystemLine($"LEN={Rt950Protocol.UnlockFrame.Length}");
        AppendSystemLine($"HEX={Hex(Rt950Protocol.UnlockFrame)}");
        AppendSystemLine("WRITE UUID=FF31");

        _rt950UnlockState = Rt950UnlockState.Ff31UnlockWrite;
        var responseTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rt950UnlockResponseTcs = responseTcs;

        GattWriteResult? writeResult = null;
        await _gattOperationGate.WaitAsync();
        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(Rt950Protocol.UnlockFrame);
            IBuffer buffer = writer.DetachBuffer();
            writeResult = await unlockCharacteristic.WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithResponse);
        }
        catch (Exception ex)
        {
            AppendSystemLine($"RT950 UNLOCK WRITE ERROR HRESULT=0x{ex.HResult:X8} exception={ex.Message}");
        }
        finally
        {
            _gattOperationGate.Release();
        }

        if (writeResult?.Status != GattCommunicationStatus.Success)
        {
            MarkRt950UnlockFailed(writeResult == null ? "WRITE_EXCEPTION" : $"WRITE_STATUS_{writeResult.Status}");
            return;
        }

        AppendSystemLine("RT950 UNLOCK WRITE SUCCESS");
        _rt950UnlockState = Rt950UnlockState.WaitUnlockResponse;
        Task completed = await Task.WhenAny(responseTcs.Task, Task.Delay(TimeSpan.FromSeconds(4)));
        if (completed != responseTcs.Task || responseTcs.Task.IsCanceled || responseTcs.Task.IsFaulted)
        {
            MarkRt950UnlockFailed("UNLOCK_RESPONSE_TIMEOUT");
            return;
        }

        _ = await responseTcs.Task;
        _rt950Unlocked = true;
        _rt950UnlockState = Rt950UnlockState.Ready;
        _writeCharacteristic = dataCharacteristic;
        WriteUuidTextBox.Text = BleUuid.Full(dataCharacteristic.Uuid);
        SelectGlobalWriteChoice(dataCharacteristic.Uuid);
        AppendSystemLine("RT950 BLE DATA PATH READY");
        AppendSystemLine("DATA_WRITE_UUID=FFE1");
        AppendSystemLine("NOTIFY_UUID=FFE1");
        SetStatus("RT950 READY (BLE unlock PASS; normal data FFE1)");
        UpdateOptionalFeatureMenuState();
        UpdateTxSendAvailability();
    }

    private void MarkRt950UnlockFailed(string reason)
    {
        _rt950Unlocked = false;
        _rt950UnlockState = Rt950UnlockState.Failed;
        _rt950UnlockResponseTcs?.TrySetCanceled();
        _rt950UnlockResponseTcs = null;
        AppendSystemLine("RT950 UNLOCK FAILED");
        AppendSystemLine($"REASON={reason}");
        SetStatus("BLE connected; RT950 unlock failed (generic BLE connection retained)");
        UpdateOptionalFeatureMenuState();
    }

    private bool TryResolveRt950Transport(
        bool requireRecognizedIdentity,
        out GattCharacteristic? dataCharacteristic,
        out GattCharacteristic? unlockCharacteristic,
        out string reason)
    {
        dataCharacteristic = null;
        unlockCharacteristic = null;
        reason = string.Empty;
        if (_service == null || !BleUuid.Is(_service.Uuid, "FFE0"))
        {
            reason = "FFE0_SERVICE_NOT_ACTIVE";
            return false;
        }

        List<GattCharacteristic> characteristics = CurrentServiceCharacteristics();
        dataCharacteristic = characteristics.FirstOrDefault(c => BleUuid.Is(c.Uuid, "FFE1") && IsWritableCharacteristic(c));
        unlockCharacteristic = characteristics.FirstOrDefault(c =>
            BleUuid.Is(c.Uuid, "FF31") && c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write));

        if (dataCharacteristic == null)
        {
            reason = "FFE1_DATA_WRITE_NOT_FOUND";
            return false;
        }
        if (unlockCharacteristic == null)
        {
            reason = "FF31_WITH_RESPONSE_NOT_FOUND";
            return false;
        }

        if (requireRecognizedIdentity && !_autoGatt.IsRadtelRt950Kiss && !IsRadtelDeviceName(_device?.Name))
        {
            reason = "RT950_IDENTITY_NOT_CONFIRMED";
            return false;
        }
        return true;
    }

    private async Task ApplyRt950GattPresetAsync()
    {
        if (!_optionalFeatureSettings.Rt950ToolsEnabled)
        {
            MessageBox.Show(this, "Enable RT950 Tools first.", "RT950 GATT Preset", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryResolveRt950Transport(requireRecognizedIdentity: false, out GattCharacteristic? dataCharacteristic, out GattCharacteristic? unlockCharacteristic, out string reason) || dataCharacteristic == null || unlockCharacteristic == null)
        {
            MessageBox.Show(this, $"RT950 GATT preset unavailable: {reason}", "RT950 GATT Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        GattCharacteristic? notify = CurrentServiceCharacteristics().FirstOrDefault(c => BleUuid.Is(c.Uuid, "FFE1") &&
            (c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) || c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate)));
        if (notify == null)
        {
            MessageBox.Show(this, "FFE1 Notify was not found.", "RT950 GATT Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_notifyCharacteristic == null || _notifyCharacteristic.Uuid != notify.Uuid)
            await SwitchNotifyCharacteristicAsync(notify);

        _routingUiUpdating = true;
        try
        {
            _writeCharacteristic = dataCharacteristic;
            SelectGlobalWriteChoice(dataCharacteristic.Uuid);
            WriteTypeComboBox.SelectedIndex = 0;
            TxModeComboBox.SelectedIndex = 1;
            LineEndingComboBox.SelectedIndex = 0;
            WriteUuidTextBox.Text = BleUuid.Full(dataCharacteristic.Uuid);
        }
        finally
        {
            _routingUiUpdating = false;
        }

        AppendSystemLine("RT950 GATT PRESET APPLIED");
        AppendSystemLine("SERVICE=FFE0");
        AppendSystemLine("NOTIFY=FFE1");
        AppendSystemLine("WRITE=FFE1");
        AppendSystemLine("UNLOCK_WRITE=FF31");
        AppendSystemLine("WRITE_TYPE=WITH_RESPONSE");
        AppendSystemLine("TX_MODE=HEX");
        AppendSystemLine("LINE_ENDING=NONE");
        AppendSystemLine("DATA_SENT=NO");
        UpdateTxSendAvailability();
        UpdateOptionalFeatureMenuState();
    }

    private void SelectGlobalWriteChoice(Guid uuid)
    {
        if (WriteRouteComboBox?.ItemsSource is not IEnumerable<GattCharacteristicChoice> choices)
            return;
        GattCharacteristicChoice? choice = choices.FirstOrDefault(c => c.Characteristic.Uuid == uuid);
        if (choice != null)
            WriteRouteComboBox.SelectedItem = choice;
    }

    private void OptionalFeatureNotification_RecordAdded(NotificationRecord record)
    {
        if (!_optionalFeatureSettings.Rt950ToolsEnabled || !BleUuid.Is(record.CharacteristicUuid, "FFE1"))
            return;

        if ((_rt950UnlockState == Rt950UnlockState.Ff31UnlockWrite || _rt950UnlockState == Rt950UnlockState.WaitUnlockResponse) &&
            Rt950Protocol.IsUnlockResponse(record.Data))
        {
            byte[] response = record.Data.ToArray();
            Dispatcher.BeginInvoke(() =>
            {
                AppendSystemLine("RT950 UNLOCK RESPONSE");
                AppendSystemLine("SOURCE_UUID=FFE1");
                AppendSystemLine($"LEN={response.Length}");
                AppendSystemLine($"HEX={Hex(response)}");
                AppendSystemLine("CLASSIFICATION=RT950_UNLOCK_RESPONSE");
            });
            _rt950UnlockResponseTcs?.TrySetResult(response);
            return;
        }

        if (IsRt950DataPathReady && Rt950Protocol.IsOemAck(record.Data))
            Dispatcher.BeginInvoke(() => AppendSystemLine("RT950 OEM ACK RECEIVED: 06"));
    }

    private void LogKissFeatureFrame(GattCharacteristic characteristic, KissFrame frame)
    {
        if (!_optionalFeatureSettings.KissToolsEnabled || !_optionalFeatureSettings.KissShowRawFrames)
            return;
        AppendSystemLine("KISS RX");
        AppendSystemLine($"SOURCE={BleUuid.Short(characteristic.Uuid)}");
        AppendSystemLine($"LEN={frame.Payload.Length}");
        AppendSystemLine($"PORT/CMD={(frame.PortCommand.HasValue ? frame.PortCommand.Value.ToString("X2") : "--")}");
        AppendSystemLine($"RAW HEX={Hex(frame.Raw)}");
        AppendSystemLine($"UNESCAPED={Hex(frame.Payload)}");
        foreach (string warning in frame.Warnings)
            AppendSystemLine($"KISS WARNING={warning}");
    }

    private void ClearKissFeatureState(bool log = true)
    {
        _notificationMonitorKissDecoder.Reset();
        _autoKissDecoder.Reset();
        if (log)
            AppendSystemLine("KISS STATE CLEARED");
    }

    private void ShowRt950Diagnostics()
    {
        string service = _service == null ? "-" : BleUuid.Short(_service.Uuid);
        string notify = _notifyCharacteristic == null ? "-" : BleUuid.Short(_notifyCharacteristic.Uuid);
        string write = _writeCharacteristic == null ? "-" : BleUuid.Short(_writeCharacteristic.Uuid);
        bool ff31 = CurrentServiceCharacteristics().Any(c => BleUuid.Is(c.Uuid, "FF31"));
        string text =
            $"RT950 Tools: {(_optionalFeatureSettings.Rt950ToolsEnabled ? "ENABLED" : "DISABLED")}\n" +
            $"Connected: {(_bleConnected ? "YES" : "NO")}\n" +
            $"Service: {service}\n" +
            $"FFE1 Notify: {(_ffe1CccdEnabled && BleUuid.Is(_notifyCharacteristic?.Uuid ?? Guid.Empty, "FFE1") ? "ACTIVE" : "NOT READY")}\n" +
            $"FF31: {(ff31 ? "FOUND" : "NOT FOUND")}\n" +
            $"BLE Unlock: {(_rt950Unlocked ? "PASS" : _rt950UnlockState.ToString().ToUpperInvariant())}\n" +
            $"Data TX: {write}\n" +
            $"KISS: {(_optionalFeatureSettings.KissToolsEnabled ? "ON" : "OFF")}";
        AppendSystemLine($"RT950 DIAGNOSTICS state={_rt950UnlockState} service={service} notify={notify} write={write} ff31={(ff31 ? "YES" : "NO")}");
        MessageBox.Show(this, text, "RT950 Diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowKissFrameBuilder()
    {
        if (!_optionalFeatureSettings.KissToolsEnabled)
            return;

        var window = new Window
        {
            Title = "KISS Frame Builder",
            Width = 720,
            Height = 330,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("WindowBackgroundBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("ForegroundBrush")
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "AX.25 payload (HEX):", Margin = new Thickness(0, 0, 0, 5) });
        var input = new TextBox { FontFamily = new System.Windows.Media.FontFamily("Consolas"), MinHeight = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(input);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        controls.Children.Add(new TextBlock { Text = "Port:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
        var port = new TextBox { Text = "0", Width = 45, Margin = new Thickness(0, 0, 8, 0) };
        controls.Children.Add(port);
        var build = new Button { Content = "BUILD KISS FRAME", MinWidth = 130 };
        controls.Children.Add(build);
        panel.Children.Add(controls);
        var output = new TextBox { FontFamily = new System.Windows.Media.FontFamily("Consolas"), IsReadOnly = true, MinHeight = 70, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(output);
        build.Click += (_, _) =>
        {
            try
            {
                byte[] ax25 = TxPayloadBuilder.ParseHex(input.Text);
                if (!byte.TryParse(port.Text.Trim(), out byte portValue) || portValue > 15)
                    throw new FormatException("KISS port must be 0 through 15.");
                output.Text = Hex(KissFrameBuilder.BuildDataFrame(ax25, portValue));
            }
            catch (Exception ex)
            {
                output.Text = $"ERROR: {ex.Message}";
            }
        };
        window.Content = panel;
        window.Show();
    }

    private void UpdateOptionalFeatureMenuState()
    {
        bool rt = _optionalFeatureSettings.Rt950ToolsEnabled;
        if (_rt950EnableMenuItem != null) _rt950EnableMenuItem.IsChecked = rt;
        if (_rt950PresetMenuItem != null) _rt950PresetMenuItem.IsEnabled = rt;
        if (_rt950UnlockMenuItem != null) _rt950UnlockMenuItem.IsEnabled = rt && _bleConnected;
        if (_rt950AutoUnlockMenuItem != null)
        {
            _rt950AutoUnlockMenuItem.IsEnabled = rt;
            _rt950AutoUnlockMenuItem.IsChecked = _optionalFeatureSettings.Rt950AutoUnlockOnConnect;
        }
        if (_rt950LoadCommandsMenuItem != null) _rt950LoadCommandsMenuItem.IsEnabled = rt;
        if (_rt950DiagnosticsMenuItem != null) _rt950DiagnosticsMenuItem.IsEnabled = rt;

        bool kiss = _optionalFeatureSettings.KissToolsEnabled;
        if (_kissEnableMenuItem != null) _kissEnableMenuItem.IsChecked = kiss;
        if (_kissDecoderMenuItem != null)
        {
            _kissDecoderMenuItem.IsEnabled = kiss;
            _kissDecoderMenuItem.IsChecked = _optionalFeatureSettings.KissRxDecoderEnabled;
        }
        if (_kissRawMenuItem != null)
        {
            _kissRawMenuItem.IsEnabled = kiss;
            _kissRawMenuItem.IsChecked = _optionalFeatureSettings.KissShowRawFrames;
        }
        if (_kissDecodedMenuItem != null)
        {
            _kissDecodedMenuItem.IsEnabled = kiss;
            _kissDecodedMenuItem.IsChecked = _optionalFeatureSettings.KissShowDecoded;
        }
        if (_kissBuilderMenuItem != null) _kissBuilderMenuItem.IsEnabled = kiss;
        if (_kissClearMenuItem != null) _kissClearMenuItem.IsEnabled = kiss;
    }

    private void LoadOptionalFeatureSettings()
    {
        _optionalFeatureSettings = new OptionalFeatureSettings();
        try
        {
            if (!System.IO.File.Exists(OptionalFeatureSettingsFile))
                return;
            _optionalFeatureSettings = JsonSerializer.Deserialize<OptionalFeatureSettings>(System.IO.File.ReadAllText(OptionalFeatureSettingsFile)) ?? new OptionalFeatureSettings();
        }
        catch
        {
            _optionalFeatureSettings = new OptionalFeatureSettings();
        }
    }

    private void SaveOptionalFeatureSettings()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(_optionalFeatureSettings, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(OptionalFeatureSettingsFile, json);
        }
        catch
        {
            // Optional feature preferences must never interfere with the generic BLE terminal.
        }
    }
}
