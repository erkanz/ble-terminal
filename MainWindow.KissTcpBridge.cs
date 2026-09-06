using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private KissTcpBridgeServer? _kissTcpBridge;
    private KissTcpBridgeWindow? _kissTcpBridgeWindow;
    private DispatcherTimer? _kissTcpBridgeStateTimer;
    private MenuItem? _kissTcpBridgeMenuItem;

    private void InitializeKissTcpBridge()
    {
        if (_kissTcpBridge != null)
            return;

        _kissTcpBridge = new KissTcpBridgeServer(WriteKissTcpBridgeBytesToBleAsync);
        _kissTcpBridge.EventOccurred += KissTcpBridge_EventOccurred;
        NotificationCaptureHub.Store.RecordAdded += KissTcpBridge_NotificationRecordAdded;
        InstallKissTcpBridgeMenuItem();

        _kissTcpBridgeStateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _kissTcpBridgeStateTimer.Tick += KissTcpBridgeStateTimer_Tick;
        _kissTcpBridgeStateTimer.Start();
        SyncKissTcpBridgeBleState();
    }

    private void ShutdownKissTcpBridge()
    {
        NotificationCaptureHub.Store.RecordAdded -= KissTcpBridge_NotificationRecordAdded;
        if (_kissTcpBridge != null)
            _kissTcpBridge.EventOccurred -= KissTcpBridge_EventOccurred;

        if (_kissTcpBridgeStateTimer != null)
        {
            _kissTcpBridgeStateTimer.Stop();
            _kissTcpBridgeStateTimer.Tick -= KissTcpBridgeStateTimer_Tick;
            _kissTcpBridgeStateTimer = null;
        }

        try { _kissTcpBridgeWindow?.Close(); } catch { }
        _kissTcpBridgeWindow = null;

        if (_kissTcpBridge != null)
        {
            try { _kissTcpBridge.StopAsync().GetAwaiter().GetResult(); } catch { }
            _kissTcpBridge = null;
        }

        if (_kissTcpBridgeMenuItem != null)
        {
            _kissTcpBridgeMenuItem.Click -= KissTcpBridgeMenuItem_Click;
            _kissTcpBridgeMenuItem = null;
        }
    }

    private void InstallKissTcpBridgeMenuItem()
    {
        if (Content is not DockPanel dock)
            return;

        Menu? menu = dock.Children.OfType<Menu>().FirstOrDefault();
        MenuItem? view = menu?.Items.OfType<MenuItem>()
            .FirstOrDefault(item => (item.Header?.ToString() ?? string.Empty)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Equals("View", StringComparison.OrdinalIgnoreCase));
        if (view == null)
            return;

        if (view.Items.OfType<MenuItem>().Any(item =>
                string.Equals(item.Header?.ToString(), "KISS TCP Bridge...", StringComparison.Ordinal)))
            return;

        view.Items.Add(new Separator());
        _kissTcpBridgeMenuItem = new MenuItem { Header = "KISS TCP Bridge..." };
        _kissTcpBridgeMenuItem.Click += KissTcpBridgeMenuItem_Click;
        view.Items.Add(_kissTcpBridgeMenuItem);
    }

    private void KissTcpBridgeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_kissTcpBridge == null)
            return;

        if (_kissTcpBridgeWindow is { IsLoaded: true })
        {
            _kissTcpBridgeWindow.Activate();
            return;
        }

        SyncKissTcpBridgeBleState();
        _kissTcpBridgeWindow = new KissTcpBridgeWindow(_kissTcpBridge)
        {
            Owner = this
        };
        _kissTcpBridgeWindow.Closed += (_, _) => _kissTcpBridgeWindow = null;
        _kissTcpBridgeWindow.Show();
    }

    private void KissTcpBridgeStateTimer_Tick(object? sender, EventArgs e) => SyncKissTcpBridgeBleState();

    private void SyncKissTcpBridgeBleState()
    {
        if (_kissTcpBridge == null)
            return;

        GattCharacteristic? bridgeWrite = _autoGatt.IsAvailable
            ? _autoGatt.WriteCharacteristic
            : _writeCharacteristic;

        bool writable = bridgeWrite != null &&
                        (bridgeWrite.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse) ||
                         bridgeWrite.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write));
        bool notificationReady = _autoGatt.IsAvailable
            ? _autoGatt.CccdEnabled
            : _notifyHandlerCharacteristic != null && _servicesDiscovered;
        bool ready = _bleConnected &&
                     _device?.ConnectionStatus == BluetoothConnectionStatus.Connected &&
                     _connectedAddress.HasValue &&
                     _connectedAt.HasValue &&
                     _service != null &&
                     bridgeWrite != null &&
                     writable &&
                     notificationReady;

        string key = ready
            ? $"{_connectedAddress!.Value:X12}|{_connectedAt!.Value.Ticks}|{_service!.Uuid:D}|{bridgeWrite!.Uuid:D}"
            : string.Empty;
        string reason = ready
            ? $"{(_autoGatt.IsAvailable ? _autoGatt.ProfileName : "Custom GATT profile")} / write={BleUuid.Short(bridgeWrite!.Uuid)}"
            : "no qualified live BLE UART/KISS route";

        _kissTcpBridge.SetBleAvailable(ready, key, reason);
    }

    private void KissTcpBridge_NotificationRecordAdded(NotificationRecord record)
    {
        try
        {
            // Main terminal notifications are tagged AUTO-DETECT or MANUAL. Inspector-only
            // subscriptions use their own source and must not become duplicate bridge traffic.
            if (!record.Source.Equals("AUTO-DETECT", StringComparison.OrdinalIgnoreCase) &&
                !record.Source.Equals("MANUAL", StringComparison.OrdinalIgnoreCase))
                return;

            _kissTcpBridge?.BroadcastBleRx(record.Data);
        }
        catch
        {
            // TCP bridging is an observer of the qualified notification path and must never
            // interfere with BLE RX, KISS parsing, Notification Monitor or the Inspector.
        }
    }

    private void KissTcpBridge_EventOccurred(KissTcpBridgeEvent evt)
    {
        try
        {
            LogCategory category = evt.Kind switch
            {
                KissTcpBridgeEventKind.Error => LogCategory.ERROR,
                KissTcpBridgeEventKind.Warning => LogCategory.WARNING,
                _ => LogCategory.KISS
            };
            _structuredLogStore.Add(
                category,
                $"KISS TCP BRIDGE {evt.Message}",
                device: CurrentLogDevice(),
                characteristic: "TCP",
                isWarning: evt.Kind == KissTcpBridgeEventKind.Warning,
                isError: evt.Kind == KissTcpBridgeEventKind.Error,
                timestamp: evt.Timestamp);
        }
        catch
        {
        }
    }

    private Task<KissTcpBridgeBleWriteResult> WriteKissTcpBridgeBytesToBleAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (Dispatcher.CheckAccess())
            return WriteKissTcpBridgeBytesToBleOnUiAsync(data, cancellationToken);

        return Dispatcher.InvokeAsync(
                () => WriteKissTcpBridgeBytesToBleOnUiAsync(data, cancellationToken),
                DispatcherPriority.Background)
            .Task.Unwrap();
    }

    private async Task<KissTcpBridgeBleWriteResult> WriteKissTcpBridgeBytesToBleOnUiAsync(
        byte[] data,
        CancellationToken cancellationToken)
    {
        if (data == null || data.Length == 0)
            return KissTcpBridgeBleWriteResult.Ok(0, "No bytes");

        GattCharacteristic? write = _autoGatt.IsAvailable
            ? _autoGatt.WriteCharacteristic
            : _writeCharacteristic;
        GattDeviceService? service = _service;
        ulong? address = _connectedAddress;
        DateTime? connectedAt = _connectedAt;

        if (!_bleConnected || _device?.ConnectionStatus != BluetoothConnectionStatus.Connected ||
            service == null || write == null || !address.HasValue || !connectedAt.HasValue)
            return KissTcpBridgeBleWriteResult.Fail("BLE connection is not available.");

        if (_autoGatt.IsAvailable && !_autoGatt.CccdEnabled)
            return KissTcpBridgeBleWriteResult.Fail("BLE notification subscription is not ready.");

        bool canNoResponse = write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        bool canResponse = write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
        if (!canNoResponse && !canResponse)
            return KissTcpBridgeBleWriteResult.Fail($"Characteristic {BleUuid.Short(write.Uuid)} is not writable.");

        GattWriteOption writeOption = canNoResponse
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
        int chunkSize = 20;
        if (int.TryParse(ChunkSizeTextBox.Text.Trim(), out int configuredChunk) && configuredChunk is >= 1 and <= 512)
            chunkSize = configuredChunk;

        Guid serviceUuid = service.Uuid;
        Guid writeUuid = write.Uuid;
        long connectedTicks = connectedAt.Value.Ticks;
        string deviceName = CurrentLogDevice();

        await _gattOperationGate.WaitAsync(cancellationToken);
        try
        {
            GattCharacteristic? currentBridgeWrite = _autoGatt.IsAvailable
                ? _autoGatt.WriteCharacteristic
                : _writeCharacteristic;
            if (!_connectedAddress.HasValue || !_connectedAt.HasValue ||
                _connectedAddress.Value != address.Value ||
                _connectedAt.Value.Ticks != connectedTicks ||
                _service == null || _service.Uuid != serviceUuid ||
                currentBridgeWrite == null || !ReferenceEquals(currentBridgeWrite, write) || currentBridgeWrite.Uuid != writeUuid)
            {
                return KissTcpBridgeBleWriteResult.Fail("BLE connection/route changed before the queued write executed.");
            }

            for (int offset = 0; offset < data.Length; offset += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(chunkSize, data.Length - offset);
                byte[] chunk = data.AsSpan(offset, count).ToArray();
                using var writer = new DataWriter();
                writer.WriteBytes(chunk);
                IBuffer buffer = writer.DetachBuffer();
                GattWriteResult result = await write.WriteValueWithResultAsync(buffer, writeOption);
                if (result.Status != GattCommunicationStatus.Success)
                {
                    string protocol = result.ProtocolError.HasValue ? $"0x{result.ProtocolError.Value:X2}" : "none";
                    return KissTcpBridgeBleWriteResult.Fail(
                        $"GATT write failed at byte {offset}: status={result.Status} statusCode={(int)result.Status} protocol={protocol}.");
                }
            }

            Interlocked.Add(ref _txBytes, data.Length);
            UpdateCounters();
            _structuredLogStore.Add(
                LogCategory.TX_RAW,
                $"KISS TCP BRIDGE BLE WRITE len={data.Length} write={BleUuid.Short(writeUuid)} option={writeOption} hex={BitConverter.ToString(data).Replace('-', ' ')}",
                direction: "TX",
                device: deviceName,
                characteristic: BleUuid.Short(writeUuid),
                data: data);
            return KissTcpBridgeBleWriteResult.Ok(data.Length,
                $"{BleUuid.Short(serviceUuid)}/{BleUuid.Short(writeUuid)} {writeOption}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return KissTcpBridgeBleWriteResult.Fail("Write cancelled.");
        }
        catch (Exception ex)
        {
            return KissTcpBridgeBleWriteResult.Fail($"BLE write exception 0x{ex.HResult:X8}: {ex.Message}");
        }
        finally
        {
            _gattOperationGate.Release();
        }
    }
}
