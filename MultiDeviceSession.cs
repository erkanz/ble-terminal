using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BLESerialTerminal;

/// <summary>
/// One completely independent live BLE connection for Phase H. No live GATT wrapper,
/// semaphore, KISS decoder or counter in this class is static/shared with another device.
/// The existing MainWindow single-device connection is not reused by this class.
/// </summary>
internal sealed class MultiDeviceSession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gattOperationGate = new(1, 1);
    private readonly SemaphoreSlim _txQueueGate = new(1, 1);
    private KissStreamDecoder _kissDecoder = new();

    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _writeCharacteristic;
    private GattCharacteristic? _notifyCharacteristic;
    private List<GattCharacteristic> _serviceCharacteristics = new();
    private CancellationTokenSource? _lifetimeCts;
    private long _generation;
    private bool _cccdReady;
    private string _state = "DISCONNECTED";
    private string _profile = "-";
    private string _lastError = string.Empty;
    private long _notifications;
    private long _rxBytes;
    private long _kissFrames;
    private long _ax25Frames;
    private long _aprsPackets;
    private long _txBytes;
    private string _lastSource = "-";
    private string _lastDestination = "-";
    private string _lastPath = "-";
    private string _lastAprsType = "-";
    private string _lastSummary = "-";

    public MultiDeviceSession(ulong address, string advertisedName)
    {
        Address = address;
        AdvertisedName = string.IsNullOrWhiteSpace(advertisedName) ? "Unnamed BLE device" : advertisedName;
        SessionId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    }

    public event Action<MultiDeviceSession>? Updated;
    public event Action<MultiDeviceSession, string>? Diagnostic;

    public string SessionId { get; }
    public ulong Address { get; }
    public string AdvertisedName { get; }

    public MultiDeviceSessionSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                string deviceName = string.IsNullOrWhiteSpace(_device?.Name) ? AdvertisedName : _device!.Name;
                return new MultiDeviceSessionSnapshot(
                    SessionId,
                    deviceName,
                    MultiDeviceDiscoveryItem.FormatAddress(Address),
                    _state,
                    _profile,
                    _service == null ? "-" : BleUuid.Short(_service.Uuid),
                    _writeCharacteristic == null ? "-" : BleUuid.Short(_writeCharacteristic.Uuid),
                    _notifyCharacteristic == null ? "-" : BleUuid.Short(_notifyCharacteristic.Uuid),
                    _writeCharacteristic != null && ReferenceEquals(_writeCharacteristic, _notifyCharacteristic),
                    _cccdReady,
                    _notifications,
                    _rxBytes,
                    _kissFrames,
                    _ax25Frames,
                    _aprsPackets,
                    _txBytes,
                    _lastSource,
                    _lastDestination,
                    _lastPath,
                    _lastAprsType,
                    _lastSummary,
                    _lastError);
            }
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        long generation = Interlocked.Increment(ref _generation);
        await DisconnectCoreAsync(writeCccdNone: false).ConfigureAwait(false);
        generation = Interlocked.Increment(ref _generation);

        SetState("CONNECTING", clearError: true);
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _lifetimeCts.Token;

        try
        {
            BluetoothLEDevice? device = await BluetoothLEDevice.FromBluetoothAddressAsync(Address);
            token.ThrowIfCancellationRequested();
            if (device == null)
                throw new InvalidOperationException("Windows could not open the BLE device.");

            if (generation != Volatile.Read(ref _generation))
            {
                device.Dispose();
                return;
            }

            _device = device;
            _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;
            SetState("BLE_CONNECTED");

            await AutoDetectAsync(device, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (_notifyCharacteristic == null)
                throw new InvalidOperationException("Auto Detect did not retain a notify characteristic.");

            _notifyCharacteristic.ValueChanged += NotifyCharacteristic_ValueChanged;
            DiagnosticLine($"HANDLER ATTACHED {BleUuid.Short(_notifyCharacteristic.Uuid)}");

            bool enabled = await EnableNotificationsAsync(_notifyCharacteristic, token).ConfigureAwait(false);
            if (!enabled)
                throw new InvalidOperationException("CCCD enable failed for the selected receive characteristic.");

            SetState("READY");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetError("Connection cancelled.");
            await DisconnectCoreAsync(writeCccdNone: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetError($"0x{ex.HResult:X8}: {ex.Message}");
            await DisconnectCoreAsync(writeCccdNone: false).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync()
    {
        Interlocked.Increment(ref _generation);
        try { _lifetimeCts?.Cancel(); } catch { }
        await DisconnectCoreAsync(writeCccdNone: true).ConfigureAwait(false);
        SetState("DISCONNECTED");
    }

    public async Task<KissTcpBridgeBleWriteResult> WriteKissAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (data == null || data.Length == 0)
            return KissTcpBridgeBleWriteResult.Ok(0, "No bytes");

        await _txQueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long generation = Volatile.Read(ref _generation);
            GattCharacteristic? write = _writeCharacteristic;
            BluetoothLEDevice? device = _device;
            if (write == null || device?.ConnectionStatus != BluetoothConnectionStatus.Connected || !_cccdReady)
                return KissTcpBridgeBleWriteResult.Fail("Session is not ready for KISS TX.");

            bool noResponse = write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
            bool withResponse = write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
            if (!noResponse && !withResponse)
                return KissTcpBridgeBleWriteResult.Fail("Selected write characteristic is not writable.");

            GattWriteOption option = noResponse ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
            await _gattOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (generation != Volatile.Read(ref _generation) ||
                    !ReferenceEquals(write, _writeCharacteristic) ||
                    _device?.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    return KissTcpBridgeBleWriteResult.Fail("Connection context changed before TX executed.");

                const int chunkSize = 20;
                for (int offset = 0; offset < data.Length; offset += chunkSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] chunk = data.AsSpan(offset, Math.Min(chunkSize, data.Length - offset)).ToArray();
                    using var writer = new DataWriter();
                    writer.WriteBytes(chunk);
                    IBuffer buffer = writer.DetachBuffer();
                    GattWriteResult result = await write.WriteValueWithResultAsync(buffer, option);
                    if (result.Status != GattCommunicationStatus.Success)
                    {
                        string protocol = result.ProtocolError.HasValue ? $"0x{result.ProtocolError.Value:X2}" : "none";
                        return KissTcpBridgeBleWriteResult.Fail(
                            $"GATT status={result.Status} statusCode={(int)result.Status} protocol={protocol}");
                    }
                }
            }
            finally
            {
                _gattOperationGate.Release();
            }

            lock (_sync)
                _txBytes += data.Length;
            DiagnosticLine($"TX {data.Length} bytes via {BleUuid.Short(write.Uuid)}");
            RaiseUpdated();
            return KissTcpBridgeBleWriteResult.Ok(data.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetError($"TX 0x{ex.HResult:X8}: {ex.Message}");
            return KissTcpBridgeBleWriteResult.Fail(ex.Message);
        }
        finally
        {
            _txQueueGate.Release();
        }
    }

    private async Task AutoDetectAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        await _gattOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GattDeviceServicesResult servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                throw new InvalidOperationException($"GATT services unavailable: {servicesResult.Status}.");

            GattDeviceService? bestService = null;
            GattCharacteristic? bestWrite = null;
            GattCharacteristic? bestNotify = null;
            List<GattCharacteristic>? bestCharacteristics = null;
            int bestScore = int.MinValue;
            string bestProfile = string.Empty;

            foreach (GattDeviceService service in servicesResult.Services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsBluetoothSigStandardService(service.Uuid))
                {
                    TryDispose(service);
                    continue;
                }

                GattCharacteristicsResult chars;
                try
                {
                    chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                }
                catch
                {
                    TryDispose(service);
                    continue;
                }

                if (chars.Status != GattCommunicationStatus.Success)
                {
                    TryDispose(service);
                    continue;
                }

                List<GattCharacteristic> writes = chars.Characteristics.Where(c =>
                    c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse) ||
                    c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)).ToList();
                List<GattCharacteristic> notifies = chars.Characteristics.Where(c =>
                    c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) ||
                    c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate)).ToList();

                foreach (GattCharacteristic write in writes)
                foreach (GattCharacteristic notify in notifies)
                {
                    int score = ScoreUartCandidate(service.Uuid, write, notify, out string profile);
                    if (score <= bestScore)
                        continue;

                    if (bestService != null && !ReferenceEquals(bestService, service))
                        TryDispose(bestService);
                    bestScore = score;
                    bestService = service;
                    bestWrite = write;
                    bestNotify = notify;
                    bestCharacteristics = chars.Characteristics.ToList();
                    bestProfile = profile;
                }

                if (!ReferenceEquals(bestService, service))
                    TryDispose(service);
            }

            if (bestService == null || bestWrite == null || bestNotify == null)
                throw new InvalidOperationException("No BLE-UART style Write + Notify/Indicate pair was found.");

            bool ffe0Ffe1 = BleUuid.Is(bestService.Uuid, "FFE0") &&
                             BleUuid.Is(bestWrite.Uuid, "FFE1") &&
                             BleUuid.Is(bestNotify.Uuid, "FFE1") &&
                             bestNotify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify);
            if (ffe0Ffe1 && (IsRadtelName(device.Name) || IsRadtelName(AdvertisedName)))
                bestProfile = "RADTEL_RT950_KISS";

            _service = bestService;
            _writeCharacteristic = bestWrite;
            _notifyCharacteristic = bestNotify;
            _serviceCharacteristics = bestCharacteristics ?? new List<GattCharacteristic>();
            lock (_sync)
            {
                _profile = bestProfile;
                _state = "GATT_DISCOVERED";
            }
            DiagnosticLine($"AUTO GATT profile={bestProfile} service={BleUuid.Short(bestService.Uuid)} write={BleUuid.Short(bestWrite.Uuid)} notify={BleUuid.Short(bestNotify.Uuid)}");
            RaiseUpdated();
        }
        finally
        {
            _gattOperationGate.Release();
        }
    }

    private async Task<bool> EnableNotificationsAsync(GattCharacteristic characteristic, CancellationToken cancellationToken)
    {
        GattClientCharacteristicConfigurationDescriptorValue value;
        if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            value = GattClientCharacteristicConfigurationDescriptorValue.Notify;
        else if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
            value = GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        else
            return false;

        await _gattOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GattWriteResult result = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(value);
            bool success = result.Status == GattCommunicationStatus.Success;
            lock (_sync)
                _cccdReady = success;
            DiagnosticLine($"CCCD {BleUuid.Short(characteristic.Uuid)} value={value} status={result.Status} statusCode={(int)result.Status}");
            RaiseUpdated();
            return success;
        }
        finally
        {
            _gattOperationGate.Release();
        }
    }

    private void NotifyCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using DataReader reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] data = new byte[checked((int)reader.UnconsumedBufferLength)];
            reader.ReadBytes(data);

            List<KissFrame> frames;
            lock (_sync)
            {
                _notifications++;
                _rxBytes += data.Length;
                frames = _kissDecoder.Push(data).ToList();
                _kissFrames += frames.Count;
            }

            foreach (KissFrame frame in frames)
                DecodeFrame(frame);

            RaiseUpdated();
        }
        catch (Exception ex)
        {
            SetError($"RX 0x{ex.HResult:X8}: {ex.Message}");
        }
    }

    private void DecodeFrame(KissFrame frame)
    {
        if (!frame.IsDataCommand)
            return;
        if (!Ax25Decoder.TryDecode(frame.Data, out Ax25Packet? ax25, out _ ) || ax25 == null)
            return;

        lock (_sync)
        {
            _ax25Frames++;
            _lastSource = ax25.Source.Display;
            _lastDestination = ax25.Destination.Display;
            _lastPath = string.IsNullOrWhiteSpace(ax25.Path) ? "-" : ax25.Path;
        }

        if (!ax25.IsAprsUiFrame)
            return;

        AprsPacket aprs = AprsDecoder.Decode(ax25.Information);
        lock (_sync)
        {
            _aprsPackets++;
            _lastAprsType = aprs.Category;
            _lastSummary = aprs.Summary;
        }
    }

    private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
            return;

        lock (_sync)
        {
            _cccdReady = false;
            _state = "DISCONNECTED";
            _lastError = "BLE stack reported disconnect.";
        }
        Interlocked.Increment(ref _generation);
        DiagnosticLine("BLE DISCONNECTED");
        RaiseUpdated();
    }

    private async Task DisconnectCoreAsync(bool writeCccdNone)
    {
        CancellationTokenSource? cts = _lifetimeCts;
        _lifetimeCts = null;
        try { cts?.Cancel(); } catch { }

        GattCharacteristic? notify = _notifyCharacteristic;
        if (notify != null)
        {
            try { notify.ValueChanged -= NotifyCharacteristic_ValueChanged; } catch { }
            if (writeCccdNone && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                await _gattOperationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await notify.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
                catch { }
                finally { _gattOperationGate.Release(); }
            }
        }

        if (_device != null)
        {
            try { _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged; } catch { }
        }

        _notifyCharacteristic = null;
        _writeCharacteristic = null;
        _serviceCharacteristics.Clear();
        TryDispose(_service);
        _service = null;
        TryDispose(_device);
        _device = null;
        cts?.Dispose();

        lock (_sync)
        {
            _cccdReady = false;
            _kissDecoder = new KissStreamDecoder();
        }
    }

    private void SetState(string state, bool clearError = false)
    {
        lock (_sync)
        {
            _state = state;
            if (clearError)
                _lastError = string.Empty;
        }
        RaiseUpdated();
    }

    private void SetError(string error)
    {
        lock (_sync)
        {
            _lastError = error;
            _state = "ERROR";
            _cccdReady = false;
        }
        DiagnosticLine(error);
        RaiseUpdated();
    }

    private void DiagnosticLine(string text)
    {
        try { Diagnostic?.Invoke(this, text); } catch { }
    }

    private void RaiseUpdated()
    {
        try { Updated?.Invoke(this); } catch { }
    }

    private static int ScoreUartCandidate(Guid serviceUuid, GattCharacteristic write, GattCharacteristic notify, out string profileName)
    {
        Guid nusService = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
        Guid nusWrite = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
        Guid nusNotify = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");
        Guid hm10Service = Guid.Parse("0000FFE0-0000-1000-8000-00805F9B34FB");
        Guid hm10Data = Guid.Parse("0000FFE1-0000-1000-8000-00805F9B34FB");

        if (serviceUuid == nusService && write.Uuid == nusWrite && notify.Uuid == nusNotify)
        {
            profileName = "Nordic UART Service (NUS)";
            return 10000;
        }
        if (serviceUuid == hm10Service && write.Uuid == hm10Data && notify.Uuid == hm10Data)
        {
            profileName = "HM-10 / FFE0-FFE1 UART";
            return 9500;
        }

        int score = 100;
        if (write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)) score += 35;
        if (write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)) score += 20;
        if (notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)) score += 35;
        if (notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate)) score += 15;
        if (write.Uuid == notify.Uuid) score += 30;
        if (!IsBluetoothBaseUuid(serviceUuid)) score += 15;
        profileName = write.Uuid == notify.Uuid
            ? "Generic BLE-UART (shared Write/Notify characteristic)"
            : "Generic BLE-UART (auto detected)";
        return score;
    }

    private static bool IsRadtelName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("walkie-talkie", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("RT-950", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("RT950", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("RADTEL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBluetoothSigStandardService(Guid uuid)
    {
        string n = uuid.ToString("N");
        const string suffix = "00001000800000805f9b34fb";
        if (n.Length != 32 || !n.StartsWith("0000", StringComparison.OrdinalIgnoreCase) ||
            !n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        return ushort.TryParse(n.Substring(4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort id) &&
               id is >= 0x1800 and <= 0x18FF;
    }

    private static bool IsBluetoothBaseUuid(Guid uuid)
    {
        string n = uuid.ToString("N");
        const string suffix = "00001000800000805f9b34fb";
        return n.Length == 32 && n.StartsWith("0000", StringComparison.OrdinalIgnoreCase) &&
               n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDispose(IDisposable? disposable)
    {
        try { disposable?.Dispose(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gattOperationGate.Dispose();
        _txQueueGate.Dispose();
    }
}
