using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private readonly SerialTaskQueue _manualTxQueue = new();
    private DispatcherTimer? _gattRoutingRefreshTimer;
    private DispatcherTimer? _txPreferenceSaveTimer;
    private bool _routingUiUpdating;
    private bool _txPreferencesLoading;
    private int _txCommandRowCount = 1;
    private string _routingConnectionKey = string.Empty;
    private string _notifyReadyLoggedKey = string.Empty;

    private static string TxCommandSettingsFile => System.IO.Path.Combine(SettingsDirectory, "tx-command-settings.json");

    private void InitializeRt950TxDiagnostics()
    {
        LoadTxCommandPreferences();
        NotificationCaptureHub.Store.RecordAdded += Rt950DiagnosticNotification_RecordAdded;

        _gattRoutingRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _gattRoutingRefreshTimer.Tick += GattRoutingRefreshTimer_Tick;
        _gattRoutingRefreshTimer.Start();

        _txPreferenceSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _txPreferenceSaveTimer.Tick += TxPreferenceSaveTimer_Tick;

        UpdateTxCommandRowsUi();
        ClearLiveGattRoutingControls();
    }

    private void ShutdownRt950TxDiagnostics()
    {
        NotificationCaptureHub.Store.RecordAdded -= Rt950DiagnosticNotification_RecordAdded;

        if (_gattRoutingRefreshTimer != null)
        {
            _gattRoutingRefreshTimer.Stop();
            _gattRoutingRefreshTimer.Tick -= GattRoutingRefreshTimer_Tick;
            _gattRoutingRefreshTimer = null;
        }

        if (_txPreferenceSaveTimer != null)
        {
            _txPreferenceSaveTimer.Stop();
            _txPreferenceSaveTimer.Tick -= TxPreferenceSaveTimer_Tick;
            _txPreferenceSaveTimer = null;
        }

        SaveTxCommandPreferences();
    }

    private void GattRoutingRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!_servicesDiscovered || _service == null || !_connectedAddress.HasValue || !_connectedAt.HasValue)
        {
            if (!string.IsNullOrEmpty(_routingConnectionKey))
            {
                _routingConnectionKey = string.Empty;
                _notifyReadyLoggedKey = string.Empty;
                ClearLiveGattRoutingControls();
            }
            return;
        }

        string key = $"{_connectedAddress.Value:X12}|{_connectedAt.Value.Ticks}|{_service.Uuid:D}";
        if (!string.Equals(key, _routingConnectionKey, StringComparison.Ordinal))
        {
            _routingConnectionKey = key;
            _notifyReadyLoggedKey = string.Empty;
            PopulateLiveGattRoutingControls();
            LogCurrentGattServiceDiscovery();
        }

        LogNotifyActiveIfReady(key);
        UpdateTxSendAvailability();
    }

    private void PopulateLiveGattRoutingControls()
    {
        if (_service == null)
            return;

        List<GattCharacteristic> characteristics = CurrentServiceCharacteristics();

        _routingUiUpdating = true;
        try
        {
            ServiceRouteComboBox.ItemsSource = new[] { BleUuid.Short(_service.Uuid) };
            ServiceRouteComboBox.SelectedIndex = 0;
            ServiceRouteComboBox.IsEnabled = false; // v13 ownership model retains the active service wrapper only.

            List<GattCharacteristicChoice> notifyChoices = characteristics
                .Select(c => new GattCharacteristicChoice(c))
                .Where(c => c.CanNotify)
                .OrderBy(c => c.ShortUuid, StringComparer.OrdinalIgnoreCase)
                .ToList();
            NotifyRouteComboBox.ItemsSource = notifyChoices;
            NotifyRouteComboBox.SelectedItem = notifyChoices.FirstOrDefault(c =>
                _notifyCharacteristic != null && c.Characteristic.Uuid == _notifyCharacteristic.Uuid);

            List<GattCharacteristicChoice> writeChoices = characteristics
                .Select(c => new GattCharacteristicChoice(c))
                .OrderBy(c => c.ShortUuid, StringComparer.OrdinalIgnoreCase)
                .ToList();
            WriteRouteComboBox.ItemsSource = writeChoices;
            WriteRouteComboBox.SelectedItem = writeChoices.FirstOrDefault(c =>
                _writeCharacteristic != null && c.Characteristic.Uuid == _writeCharacteristic.Uuid);

            if (WriteTypeComboBox.SelectedIndex < 0)
                WriteTypeComboBox.SelectedIndex = 0;
        }
        finally
        {
            _routingUiUpdating = false;
        }

        RoutingStatusTextBlock.Text = "Auto profile preserved. Manual Write selection changes TX routing only.";
        UpdateTxSendAvailability();
    }

    private List<GattCharacteristic> CurrentServiceCharacteristics()
    {
        if (_service != null && _autoGatt.Service != null &&
            ReferenceEquals(_service, _autoGatt.Service) && _autoGatt.ServiceCharacteristics.Count > 0)
        {
            return _autoGatt.ServiceCharacteristics
                .GroupBy(c => c.Uuid)
                .Select(g => g.First())
                .ToList();
        }

        var result = new List<GattCharacteristic>();
        if (_notifyCharacteristic != null)
            result.Add(_notifyCharacteristic);
        if (_writeCharacteristic != null && result.All(c => c.Uuid != _writeCharacteristic.Uuid))
            result.Add(_writeCharacteristic);
        return result;
    }

    private void ClearLiveGattRoutingControls()
    {
        _routingUiUpdating = true;
        try
        {
            ServiceRouteComboBox.ItemsSource = null;
            NotifyRouteComboBox.ItemsSource = null;
            WriteRouteComboBox.ItemsSource = null;
            WriteTypeComboBox.SelectedIndex = 0;
        }
        finally
        {
            _routingUiUpdating = false;
        }

        RoutingStatusTextBlock.Text = "Connect to populate live GATT routing.";
        SendButton.IsEnabled = false;
    }

    private void LogCurrentGattServiceDiscovery()
    {
        if (_service == null)
            return;

        AppendSystemLine("GATT SERVICE");
        AppendSystemLine($"UUID={BleUuid.Short(_service.Uuid)}");

        foreach (GattCharacteristic characteristic in CurrentServiceCharacteristics())
        {
            GattCharacteristicProperties p = characteristic.CharacteristicProperties;
            AppendSystemLine("GATT CHARACTERISTIC");
            AppendSystemLine($"UUID={BleUuid.Short(characteristic.Uuid)}");
            AppendSystemLine($"READ={YesNo(p.HasFlag(GattCharacteristicProperties.Read))}");
            AppendSystemLine($"WRITE={YesNo(p.HasFlag(GattCharacteristicProperties.Write))}");
            AppendSystemLine($"WRITE_WITHOUT_RESPONSE={YesNo(p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))}");
            AppendSystemLine($"NOTIFY={YesNo(p.HasFlag(GattCharacteristicProperties.Notify))}");
            AppendSystemLine($"INDICATE={YesNo(p.HasFlag(GattCharacteristicProperties.Indicate))}");
        }
    }

    private void LogNotifyActiveIfReady(string connectionKey)
    {
        if (string.Equals(_notifyReadyLoggedKey, connectionKey, StringComparison.Ordinal))
            return;
        if (!_ffe1CccdEnabled || _service == null || _notifyCharacteristic == null ||
            !BleUuid.Is(_notifyCharacteristic.Uuid, "FFE1"))
            return;

        _notifyReadyLoggedKey = connectionKey;
        AppendSystemLine("BLE NOTIFY ACTIVE");
        AppendSystemLine($"SERVICE={BleUuid.Short(_service.Uuid)}");
        AppendSystemLine($"UUID={BleUuid.Short(_notifyCharacteristic.Uuid)}");
        AppendSystemLine("CCCD=SUCCESS");
    }

    private void WriteRouteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_routingUiUpdating || WriteRouteComboBox.SelectedItem is not GattCharacteristicChoice choice)
            return;

        // This is intentionally only the active TX route. AutoDetectedGattContext remains untouched.
        _writeCharacteristic = choice.Characteristic;
        WriteUuidTextBox.Text = BleUuid.Full(choice.Characteristic.Uuid);
        AppendSystemLine("MANUAL GATT TX ROUTE");
        AppendSystemLine($"SERVICE={(_service == null ? "-" : BleUuid.Short(_service.Uuid))}");
        AppendSystemLine($"WRITE_UUID={choice.ShortUuid}");
        AppendSystemLine($"AUTO_PROFILE_PRESERVED={(_autoGatt.IsAvailable ? "YES" : "N/A")}");
        UpdateTxSendAvailability();
    }

    private void WriteTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_routingUiUpdating)
            return;
        UpdateTxSendAvailability();
    }

    private async void NotifyRouteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_routingUiUpdating || NotifyRouteComboBox.SelectedItem is not GattCharacteristicChoice choice)
            return;
        if (_notifyCharacteristic != null && _notifyCharacteristic.Uuid == choice.Characteristic.Uuid)
            return;

        await SwitchNotifyCharacteristicAsync(choice.Characteristic);
    }

    private async Task SwitchNotifyCharacteristicAsync(GattCharacteristic target)
    {
        if (!target.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) &&
            !target.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
        {
            MessageBox.Show(this, "Selected characteristic does not support Notify or Indicate.", "GATT routing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        GattCharacteristic? previous = _notifyCharacteristic;
        SendButton.IsEnabled = false;

        try
        {
            if (previous != null)
                await DisableNotificationsForCharacteristicBestEffortAsync(previous);

            _notifyCharacteristic = target;
            NotifyUuidTextBox.Text = BleUuid.Full(target.Uuid);
            AttachNotifyHandler(target);
            SyncMainNotificationCaptureHandler();

            bool ready = await EnableTerminalNotificationsAsync(target);
            if (!ready)
                throw new InvalidOperationException($"Notify subscription failed for {BleUuid.Short(target.Uuid)}.");

            _ffe1Found = BleUuid.Is(target.Uuid, "FFE1");
            _ffe1CccdEnabled = _ffe1Found && ready;
            _radtelKissReady = _autoGatt.IsRadtelRt950Kiss && _ffe1CccdEnabled;
            AppendSystemLine($"MANUAL RX ROUTE ACTIVE UUID={BleUuid.Short(target.Uuid)}");
            _notifyReadyLoggedKey = string.Empty;
        }
        catch (Exception ex)
        {
            AppendSystemLine($"MANUAL RX ROUTE ERROR: {ex.Message}");
            if (previous != null && !ReferenceEquals(previous, target))
            {
                try
                {
                    _notifyCharacteristic = previous;
                    NotifyUuidTextBox.Text = BleUuid.Full(previous.Uuid);
                    AttachNotifyHandler(previous);
                    SyncMainNotificationCaptureHandler();
                    bool restored = await EnableTerminalNotificationsAsync(previous);
                    _ffe1Found = BleUuid.Is(previous.Uuid, "FFE1");
                    _ffe1CccdEnabled = _ffe1Found && restored;
                    _radtelKissReady = _autoGatt.IsRadtelRt950Kiss && _ffe1CccdEnabled;
                    AppendSystemLine($"RX ROUTE RESTORED UUID={BleUuid.Short(previous.Uuid)} status={(restored ? "SUCCESS" : "FAIL")}");
                }
                catch (Exception restoreEx)
                {
                    AppendSystemLine($"RX ROUTE RESTORE ERROR: {restoreEx.Message}");
                }
            }

            MessageBox.Show(this, ex.Message, "GATT notify routing failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _routingUiUpdating = true;
            try
            {
                if (NotifyRouteComboBox.ItemsSource is IEnumerable<GattCharacteristicChoice> choices && _notifyCharacteristic != null)
                {
                    NotifyRouteComboBox.SelectedItem = choices.FirstOrDefault(c => c.Characteristic.Uuid == _notifyCharacteristic.Uuid);
                }
            }
            finally
            {
                _routingUiUpdating = false;
            }
            UpdateTxSendAvailability();
        }
    }

    private async Task DisableNotificationsForCharacteristicBestEffortAsync(GattCharacteristic characteristic)
    {
        await _gattOperationGate.WaitAsync();
        try
        {
            GattWriteResult result = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None);
            AppendSystemLine($"CCCD ROUTE DISABLE uuid={BleUuid.Short(characteristic.Uuid)} status={result.Status} statusCode={(int)result.Status} protocol={ProtocolText(result.ProtocolError)}");
        }
        catch (Exception ex)
        {
            AppendSystemLine($"CCCD ROUTE DISABLE ERROR uuid={BleUuid.Short(characteristic.Uuid)} HRESULT=0x{ex.HResult:X8} exception={ex.Message}");
        }
        finally
        {
            _gattOperationGate.Release();
        }
    }

    private async void Rt950OemPresetButton_Click(object sender, RoutedEventArgs e) =>
        await ApplyRt950PresetAsync("FF31", "RT950 OEM TEST");

    private async void Rt950Ffe1PresetButton_Click(object sender, RoutedEventArgs e) =>
        await ApplyRt950PresetAsync("FFE1", "RT950 FFE1 TEST");

    private async Task ApplyRt950PresetAsync(string writeShortUuid, string presetName)
    {
        if (_service == null || !BleUuid.Is(_service.Uuid, "FFE0"))
        {
            MessageBox.Show(this, "RT950 preset requires the active FFE0 service.", presetName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        GattCharacteristicChoice? notify = (NotifyRouteComboBox.ItemsSource as IEnumerable<GattCharacteristicChoice>)?
            .FirstOrDefault(c => BleUuid.Is(c.Characteristic.Uuid, "FFE1"));
        GattCharacteristicChoice? write = (WriteRouteComboBox.ItemsSource as IEnumerable<GattCharacteristicChoice>)?
            .FirstOrDefault(c => BleUuid.Is(c.Characteristic.Uuid, writeShortUuid));

        if (notify == null)
        {
            MessageBox.Show(this, "FFE1 Notify was not found in the live FFE0 characteristic list.", presetName, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (write == null)
        {
            MessageBox.Show(this, $"{writeShortUuid} was not found in the live FFE0 characteristic list.", presetName, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _routingUiUpdating = true;
        try
        {
            WriteRouteComboBox.SelectedItem = write;
            WriteTypeComboBox.SelectedIndex = 0; // With Response
            TxModeComboBox.SelectedIndex = 1; // HEX
            LineEndingComboBox.SelectedIndex = 0; // NONE
        }
        finally
        {
            _routingUiUpdating = false;
        }

        _writeCharacteristic = write.Characteristic;
        WriteUuidTextBox.Text = BleUuid.Full(write.Characteristic.Uuid);

        if (_notifyCharacteristic == null || _notifyCharacteristic.Uuid != notify.Characteristic.Uuid)
            await SwitchNotifyCharacteristicAsync(notify.Characteristic);
        else
        {
            _routingUiUpdating = true;
            try { NotifyRouteComboBox.SelectedItem = notify; }
            finally { _routingUiUpdating = false; }
        }

        AppendSystemLine($"{presetName} PRESET APPLIED");
        AppendSystemLine("SERVICE=FFE0");
        AppendSystemLine("NOTIFY=FFE1");
        AppendSystemLine($"WRITE={writeShortUuid}");
        AppendSystemLine("WRITE_TYPE=WITH_RESPONSE");
        AppendSystemLine("TX_MODE=HEX");
        AppendSystemLine("LINE_ENDING=NONE");
        AppendSystemLine("DATA_SENT=NO");
        UpdateTxSendAvailability();
    }

    private void LoadRt950TestCommandsButton_Click(object sender, RoutedEventArgs e)
    {
        CommandLabel1TextBox.Text = "OEM Handshake";
        TxTextBox.Text = "50 52 4F 47 52 41 4D 42 54 39 30 30 30 55";
        CommandLabel2TextBox.Text = "Model Query";
        TxTextBox2.Text = "4D";
        CommandLabel3TextBox.Text = "Reserved Test";
        TxTextBox3.Text = string.Empty;
        CommandLabel4TextBox.Text = "Command 4";
        TxTextBox4.Text = string.Empty;
        CommandLabel5TextBox.Text = "Command 5";
        TxTextBox5.Text = string.Empty;

        if (_txCommandRowCount < 2)
            SetTxCommandRowCount(2, save: false);

        SaveTxCommandPreferences();
        AppendSystemLine("RT950 TEST COMMANDS LOADED; DATA_SENT=NO");
    }

    private async void SendCommandSlotButton_Click(object sender, RoutedEventArgs e)
    {
        int slot = sender is FrameworkElement element && int.TryParse(element.Tag?.ToString(), out int value) ? value : 1;
        await QueueCommandSlotAsync(slot);
    }

    private async void CommandInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        int slot = sender is FrameworkElement element && int.TryParse(element.Tag?.ToString(), out int value) ? value : 1;
        await QueueCommandSlotAsync(slot);
    }

    private async Task QueueCommandSlotAsync(int slot)
    {
        if (_service == null || _writeCharacteristic == null || !_connectedAddress.HasValue || !_connectedAt.HasValue)
        {
            MessageBox.Show(this, "Not connected.", "BLE Serial Terminal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryReadChunkSize(out int chunkSize))
            return;

        byte[] payload;
        try
        {
            payload = TxPayloadBuilder.Build(GetCommandText(slot), TxModeComboBox.SelectedIndex == 1, GetSelectedTxLineEnding());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid TX data", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (payload.Length == 0)
            return;

        bool withResponse = WriteTypeComboBox.SelectedIndex != 1;
        GattCharacteristicProperties properties = _writeCharacteristic.CharacteristicProperties;
        bool supported = withResponse
            ? properties.HasFlag(GattCharacteristicProperties.Write)
            : properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);

        if (!supported)
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_SLOT={slot}");
            AppendSystemLine($"UUID={BleUuid.Short(_writeCharacteristic.Uuid)}");
            AppendSystemLine($"REASON={(withResponse ? "WRITE_WITH_RESPONSE_NOT_SUPPORTED" : "WRITE_WITHOUT_RESPONSE_NOT_SUPPORTED")}");
            MessageBox.Show(this,
                $"{BleUuid.Short(_writeCharacteristic.Uuid)} does not support the selected write type.",
                "GATT write not supported",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            UpdateTxSendAvailability();
            return;
        }

        var request = new TxCommandRequest(
            slot,
            GetCommandLabel(slot),
            BleUuid.Full(_service.Uuid),
            BleUuid.Full(_writeCharacteristic.Uuid),
            withResponse,
            payload.ToArray(),
            _connectedAddress,
            _connectedAt,
            chunkSize);

        AppendSystemLine($"TX QUEUED COMMAND_SLOT={slot}");
        AppendSystemLine($"TX QUEUED UUID={BleUuid.Short(_writeCharacteristic.Uuid)} LEN={payload.Length}");
        SaveTxCommandPreferences();

        Task queued = _manualTxQueue.Enqueue(() =>
            Dispatcher.InvokeAsync(() => ExecuteTxRequestAsync(request), DispatcherPriority.Normal).Task.Unwrap());

        try
        {
            await queued;
        }
        catch (Exception ex)
        {
            AppendSystemLine($"TX QUEUE ERROR COMMAND_SLOT={slot}: {ex.Message}");
        }
    }

    private async Task ExecuteTxRequestAsync(TxCommandRequest request)
    {
        if (!_connectedAddress.HasValue || !_connectedAt.HasValue ||
            request.BluetoothAddress != _connectedAddress || request.ConnectedAt != _connectedAt || _service == null)
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_SLOT={request.Slot}");
            AppendSystemLine("REASON=CONNECTION_CONTEXT_CHANGED");
            return;
        }

        if (!Guid.TryParse(request.ServiceUuid, out Guid serviceUuid) || _service.Uuid != serviceUuid ||
            !Guid.TryParse(request.WriteUuid, out Guid writeUuid))
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_SLOT={request.Slot}");
            AppendSystemLine("REASON=GATT_ROUTE_CHANGED");
            return;
        }

        GattCharacteristic? characteristic = ResolveCurrentCharacteristic(writeUuid);
        if (characteristic == null)
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_SLOT={request.Slot}");
            AppendSystemLine($"UUID={BleUuid.Short(writeUuid)}");
            AppendSystemLine("REASON=CHARACTERISTIC_NOT_AVAILABLE");
            return;
        }

        GattCharacteristicProperties p = characteristic.CharacteristicProperties;
        bool supported = request.WithResponse
            ? p.HasFlag(GattCharacteristicProperties.Write)
            : p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        if (!supported)
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_SLOT={request.Slot}");
            AppendSystemLine($"UUID={BleUuid.Short(characteristic.Uuid)}");
            AppendSystemLine("REASON=SELECTED_WRITE_TYPE_NOT_SUPPORTED");
            return;
        }

        GattWriteOption option = request.WithResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        bool allSucceeded = true;

        await _gattOperationGate.WaitAsync();
        try
        {
            for (int offset = 0; offset < request.Payload.Length; offset += request.ChunkSize)
            {
                int count = Math.Min(request.ChunkSize, request.Payload.Length - offset);
                byte[] chunk = new byte[count];
                Buffer.BlockCopy(request.Payload, offset, chunk, 0, count);

                AppendSystemLine("RAW BLE WRITE");
                AppendSystemLine($"COMMAND_SLOT={request.Slot}");
                if (!string.IsNullOrWhiteSpace(request.Label))
                    AppendSystemLine($"COMMAND_LABEL={request.Label}");
                AppendSystemLine($"SERVICE={BleUuid.Short(serviceUuid)}");
                AppendSystemLine($"UUID={BleUuid.Short(characteristic.Uuid)}");
                AppendSystemLine($"TYPE={(request.WithResponse ? "WITH_RESPONSE" : "WITHOUT_RESPONSE")}");
                AppendSystemLine($"LEN={chunk.Length}");
                AppendSystemLine($"HEX={Hex(chunk)}");

                using var writer = new DataWriter();
                writer.WriteBytes(chunk);
                IBuffer buffer = writer.DetachBuffer();

                bool startAccepted = false;
                try
                {
                    var operation = characteristic.WriteValueWithResultAsync(buffer, option);
                    startAccepted = true;
                    AppendSystemLine("WRITE START RESULT=ACCEPTED");
                    AppendSystemLine($"COMMAND_SLOT={request.Slot}");

                    if (!request.WithResponse)
                    {
                        AppendSystemLine("WRITE SUBMITTED NO_RESPONSE");
                        AppendSystemLine($"COMMAND_SLOT={request.Slot}");
                        AppendSystemLine($"UUID={BleUuid.Short(characteristic.Uuid)}");
                    }

                    GattWriteResult result = await operation;
                    bool success = result.Status == GattCommunicationStatus.Success;
                    allSucceeded &= success;

                    AppendSystemLine("WRITE COMPLETE");
                    AppendSystemLine($"COMMAND_SLOT={request.Slot}");
                    AppendSystemLine($"UUID={BleUuid.Short(characteristic.Uuid)}");
                    AppendSystemLine($"STATUS={(success ? "SUCCESS" : "FAIL")}");
                    AppendSystemLine($"STATUS_CODE={(int)result.Status}");
                    AppendSystemLine($"PROTOCOL_ERROR={ProtocolText(result.ProtocolError)}");
                    AppendSystemLine($"WRITE COMPLETE COMMAND_SLOT={request.Slot} STATUS={(success ? "SUCCESS" : "FAIL")} STATUS_CODE={(int)result.Status}");

                    if (!success)
                        break;

                    if (!request.WithResponse)
                        await Task.Delay(10);
                }
                catch (Exception ex)
                {
                    allSucceeded = false;
                    if (!startAccepted)
                        AppendSystemLine("WRITE START RESULT=REJECTED");
                    else
                        AppendSystemLine("WRITE COMPLETE");
                    AppendSystemLine($"COMMAND_SLOT={request.Slot}");
                    AppendSystemLine($"UUID={BleUuid.Short(characteristic.Uuid)}");
                    AppendSystemLine("STATUS=FAIL");
                    AppendSystemLine("STATUS_CODE=EXCEPTION");
                    AppendSystemLine($"HRESULT=0x{ex.HResult:X8}");
                    AppendSystemLine($"EXCEPTION={ex.Message}");
                    break;
                }
            }
        }
        finally
        {
            _gattOperationGate.Release();
        }

        if (!allSucceeded)
            return;

        Interlocked.Add(ref _txBytes, request.Payload.Length);
        if (LocalEchoCheckBox.IsChecked == true)
            AppendTx(request.Payload);
        UpdateCounters();

        // Slot 1 is already captured by the legacy Phase D SendButton/Enter observer.
        // Additional slots do not use that routed-event observer, so capture them here.
        if (request.Slot != 1)
        {
            _structuredLogStore.Add(
                LogCategory.TX_RAW,
                $"BLE WRITE command_slot={request.Slot} label={request.Label} len={request.Payload.Length} hex={Hex(request.Payload)}",
                direction: "TX",
                device: CurrentLogDevice(),
                characteristic: BleUuid.Short(characteristic.Uuid),
                data: request.Payload);
        }
    }

    private GattCharacteristic? ResolveCurrentCharacteristic(Guid uuid)
    {
        if (_autoGatt.ServiceCharacteristics.Count > 0)
        {
            GattCharacteristic? cached = _autoGatt.ServiceCharacteristics.FirstOrDefault(c => c.Uuid == uuid);
            if (cached != null)
                return cached;
        }
        if (_writeCharacteristic?.Uuid == uuid)
            return _writeCharacteristic;
        if (_notifyCharacteristic?.Uuid == uuid)
            return _notifyCharacteristic;
        return null;
    }

    private void UpdateTxSendAvailability()
    {
        // SelectionChanged can fire while InitializeComponent() is still constructing XAML.
        // Named controls declared later in the XAML are not guaranteed to exist yet.
        if (SendButton == null || WriteTypeComboBox == null || RoutingStatusTextBlock == null)
            return;

        if (_writeCharacteristic == null || _device == null || !_bleConnected)
        {
            SendButton.IsEnabled = false;
            return;
        }

        bool withResponse = WriteTypeComboBox.SelectedIndex != 1;
        GattCharacteristicProperties p = _writeCharacteristic.CharacteristicProperties;
        bool supported = withResponse
            ? p.HasFlag(GattCharacteristicProperties.Write)
            : p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);

        SendButton.IsEnabled = supported;
        RoutingStatusTextBlock.Text = supported
            ? $"TX {BleUuid.Short(_writeCharacteristic.Uuid)} / {(withResponse ? "With Response" : "Without Response")}; RX {(_notifyCharacteristic == null ? "-" : BleUuid.Short(_notifyCharacteristic.Uuid))}"
            : $"{BleUuid.Short(_writeCharacteristic.Uuid)} does not support {(withResponse ? "Write With Response" : "Write Without Response")}. Send disabled.";
    }

    private TxLineEnding GetSelectedTxLineEnding() => LineEndingComboBox.SelectedIndex switch
    {
        1 => TxLineEnding.Cr,
        2 => TxLineEnding.Lf,
        3 => TxLineEnding.CrLf,
        _ => TxLineEnding.None
    };

    private string GetCommandText(int slot) => slot switch
    {
        2 => TxTextBox2.Text,
        3 => TxTextBox3.Text,
        4 => TxTextBox4.Text,
        5 => TxTextBox5.Text,
        _ => TxTextBox.Text
    };

    private string GetCommandLabel(int slot) => slot switch
    {
        2 => CommandLabel2TextBox.Text.Trim(),
        3 => CommandLabel3TextBox.Text.Trim(),
        4 => CommandLabel4TextBox.Text.Trim(),
        5 => CommandLabel5TextBox.Text.Trim(),
        _ => CommandLabel1TextBox.Text.Trim()
    };

    private void TxCommandRowCountMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && int.TryParse(item.Tag?.ToString(), out int count))
            SetTxCommandRowCount(count, save: true);
    }

    private void SetTxCommandRowCount(int count, bool save)
    {
        _txCommandRowCount = Math.Clamp(count, 1, 5);
        UpdateTxCommandRowsUi();
        if (save)
            SaveTxCommandPreferences();
    }

    private void UpdateTxCommandRowsUi()
    {
        if (CommandRow1 == null)
            return;

        CommandRow1.Visibility = Visibility.Visible;
        CommandRow2.Visibility = _txCommandRowCount >= 2 ? Visibility.Visible : Visibility.Collapsed;
        CommandRow3.Visibility = _txCommandRowCount >= 3 ? Visibility.Visible : Visibility.Collapsed;
        CommandRow4.Visibility = _txCommandRowCount >= 4 ? Visibility.Visible : Visibility.Collapsed;
        CommandRow5.Visibility = _txCommandRowCount >= 5 ? Visibility.Visible : Visibility.Collapsed;
        TxCommandsExpander.Header = $"TX commands ({_txCommandRowCount})";

        TxRows1MenuItem.IsChecked = _txCommandRowCount == 1;
        TxRows2MenuItem.IsChecked = _txCommandRowCount == 2;
        TxRows3MenuItem.IsChecked = _txCommandRowCount == 3;
        TxRows4MenuItem.IsChecked = _txCommandRowCount == 4;
        TxRows5MenuItem.IsChecked = _txCommandRowCount == 5;
    }

    private void CommandPreferenceTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_txPreferencesLoading || _txPreferenceSaveTimer == null)
            return;
        _txPreferenceSaveTimer.Stop();
        _txPreferenceSaveTimer.Start();
    }

    private void TxPreferenceSaveTimer_Tick(object? sender, EventArgs e)
    {
        _txPreferenceSaveTimer?.Stop();
        SaveTxCommandPreferences();
    }

    private void LoadTxCommandPreferences()
    {
        _txPreferencesLoading = true;
        try
        {
            TxCommandPreferencesData settings = new();
            if (System.IO.File.Exists(TxCommandSettingsFile))
            {
                try
                {
                    settings = JsonSerializer.Deserialize<TxCommandPreferencesData>(System.IO.File.ReadAllText(TxCommandSettingsFile)) ?? new TxCommandPreferencesData();
                }
                catch
                {
                    settings = new TxCommandPreferencesData();
                }
            }

            _txCommandRowCount = Math.Clamp(settings.RowCount, 1, 5);
            for (int slot = 1; slot <= 5; slot++)
            {
                TxCommandSlotData? saved = settings.Slots?.FirstOrDefault(x => x.Slot == slot);
                SetCommandLabel(slot, string.IsNullOrWhiteSpace(saved?.Label) ? $"Command {slot}" : saved.Label);
                SetCommandText(slot, saved?.Command ?? string.Empty);
            }
        }
        finally
        {
            _txPreferencesLoading = false;
        }
    }

    private void SaveTxCommandPreferences()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsDirectory);
            var data = new TxCommandPreferencesData
            {
                RowCount = _txCommandRowCount,
                Slots = Enumerable.Range(1, 5)
                    .Select(slot => new TxCommandSlotData
                    {
                        Slot = slot,
                        Label = GetCommandLabel(slot),
                        Command = GetCommandText(slot)
                    })
                    .ToList()
            };
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(TxCommandSettingsFile, json, new UTF8Encoding(false));
        }
        catch
        {
            // Persistence must never interfere with BLE diagnostics.
        }
    }

    private void SetCommandText(int slot, string value)
    {
        switch (slot)
        {
            case 2: TxTextBox2.Text = value; break;
            case 3: TxTextBox3.Text = value; break;
            case 4: TxTextBox4.Text = value; break;
            case 5: TxTextBox5.Text = value; break;
            default: TxTextBox.Text = value; break;
        }
    }

    private void SetCommandLabel(int slot, string value)
    {
        switch (slot)
        {
            case 2: CommandLabel2TextBox.Text = value; break;
            case 3: CommandLabel3TextBox.Text = value; break;
            case 4: CommandLabel4TextBox.Text = value; break;
            case 5: CommandLabel5TextBox.Text = value; break;
            default: CommandLabel1TextBox.Text = value; break;
        }
    }

    private void Rt950DiagnosticNotification_RecordAdded(NotificationRecord record)
    {
        if (!BleUuid.Is(record.CharacteristicUuid, "FFE1"))
            return;

        if (record.Data.Length == 1 && record.Data[0] == 0x06)
            Dispatcher.BeginInvoke(() => AppendSystemLine("RT950 OEM ACK RECEIVED: 06"));

        if (IsFullyPrintableAscii(record.Data))
        {
            string ascii = Encoding.ASCII.GetString(record.Data);
            Dispatcher.BeginInvoke(() => AppendSystemLine($"ASCII={ascii}"));
        }
    }

    private static bool IsFullyPrintableAscii(byte[] data) =>
        data.Length > 0 && data.All(b => b is >= 0x20 and <= 0x7E);
}
