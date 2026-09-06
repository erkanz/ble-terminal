using Microsoft.Win32;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BLESerialTerminal;

public partial class GattInspectorWindow : Window
{
    private readonly BluetoothLEDevice _device;
    private readonly string _deviceName;
    private readonly ulong _address;
    private readonly DateTime _connectionTimestamp;
    private readonly AutoDetectedGattContext? _autoGatt;
    private readonly SemaphoreSlim _gattOperationGate;
    private readonly List<GattServiceInfo> _services = new();
    private readonly HashSet<GattCharacteristic> _subscribedCharacteristics = new();
    private readonly Dictionary<GattCharacteristic, KissStreamDecoder> _kissDecoders = new();
    private readonly StringBuilder _debugLog = new();

    internal event Action<AutoDetectedGattContext, bool>? AutoTerminalCccdStateChanged;

    private GattCharacteristicInfo? _selectedCharacteristic;
    private GattSession? _session;
    private ushort? _maxPduSize;
    private long _rxBytes;
    private bool _dataReceived;
    private bool _closing;

    internal GattInspectorWindow(
        BluetoothLEDevice device,
        string deviceName,
        ulong address,
        DateTime connectionTimestamp,
        AutoDetectedGattContext? autoGatt,
        SemaphoreSlim gattOperationGate)
    {
        InitializeComponent();
        _device = device;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? "Unnamed BLE device" : deviceName;
        _address = address;
        _connectionTimestamp = connectionTimestamp;
        _autoGatt = autoGatt;
        _gattOperationGate = gattOperationGate;

        DeviceHeaderText.Text = $"{_deviceName}   [{FormatBluetoothAddress(_address)}]";
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        Loaded += GattInspectorWindow_Loaded;
        Closing += GattInspectorWindow_Closing;
    }

    private async void GattInspectorWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Log($"*** CONNECTED {_deviceName} [{FormatBluetoothAddress(_address)}]");
        Log($"connectionState={_device.ConnectionStatus}");
        if (_autoGatt?.IsAvailable == true)
        {
            Log($"*** AUTO TERMINAL SUBSCRIPTION PROTECTED service={BleUuid.Short(_autoGatt.Service!.Uuid)} notify={BleUuid.Short(_autoGatt.NotifyCharacteristic!.Uuid)}");
            Log($"*** AUTO CACHE profile={_autoGatt.ProfileName} sameCharacteristic={_autoGatt.SameCharacteristic.ToString().ToLowerInvariant()} handlerAttached={_autoGatt.NotifyHandlerAttached} cccdEnabled={_autoGatt.CccdEnabled}");
        }
        UpdateStatePanel();
        await DiscoverGattAsync();
    }

    private async void GattInspectorWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        await DisableAllSubscriptionsBestEffortAsync();
        DisposeDiscoveredServices();
        try
        {
            if (_session != null)
                _session.MaxPduSizeChanged -= Session_MaxPduSizeChanged;
            _session?.Dispose();
        }
        catch { }
        _session = null;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await DiscoverGattAsync();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task DiscoverGattAsync()
    {
        RefreshButton.IsEnabled = false;
        bool gateHeld = false;
        try
        {
            await DisableAllSubscriptionsBestEffortAsync();
            await _gattOperationGate.WaitAsync();
            gateHeld = true;
            DisposeDiscoveredServices();
            GattTree.Items.Clear();
            ClearSelectionDetails();
            GattStateText.Text = "GATT: DISCOVERING";

            Log("*** GATT DISCOVERY START");
            await OpenGattSessionAsync();

            int characteristicCount = 0;
            bool ff31 = false;
            bool ff32 = false;
            bool ffe1 = false;
            bool usedAutoCache = false;
            bool autoServiceSeenInEnumeration = false;

            var result = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            Log($"GetGattServicesAsync {StatusText(result.Status, result.ProtocolError)}");

            if (result.Status == GattCommunicationStatus.Success)
            {
                foreach (GattDeviceService service in result.Services)
                {
                    if (_autoGatt?.IsAvailable == true && service.Uuid == _autoGatt.Service!.Uuid)
                    {
                        autoServiceSeenInEnumeration = true;
                        if (!usedAutoCache)
                        {
                            characteristicCount += await AddAutoDetectedServiceAsync();
                            usedAutoCache = true;
                            UpdatePresenceFromService(_services.Last(), ref ff31, ref ff32, ref ffe1);
                        }

                        if (!ReferenceEquals(service, _autoGatt.Service))
                        {
                            try { service.Dispose(); } catch { }
                        }
                        continue;
                    }

                    GattServiceInfo serviceInfo = await AddEnumeratedServiceAsync(service);
                    characteristicCount += serviceInfo.Characteristics.Count;
                    UpdatePresenceFromService(serviceInfo, ref ff31, ref ff32, ref ffe1);
                }
            }
            else if (_autoGatt?.IsAvailable != true)
            {
                throw new InvalidOperationException($"GATT service discovery failed: {result.Status}, protocol={ProtocolText(result.ProtocolError)}");
            }
            else
            {
                Log($"*** SERVICE ENUMERATION FAILED BUT AUTO CACHE IS VALID status={result.Status} statusCode={(int)result.Status}");
            }

            if (_autoGatt?.IsAvailable == true && !usedAutoCache)
            {
                characteristicCount += await AddAutoDetectedServiceAsync();
                usedAutoCache = true;
                UpdatePresenceFromService(_services.Last(), ref ff31, ref ff32, ref ffe1);
                if (!autoServiceSeenInEnumeration)
                    Log("*** AUTO-DETECT SERVICE WAS NOT RETURNED BY SECOND DISCOVERY; CACHED OBJECTS RETAINED");
            }

            GattStateText.Text = result.Status == GattCommunicationStatus.Success ? "GATT: DISCOVERED" : "GATT: PARTIAL / CACHED";
            Log($"*** GATT DISCOVERY COMPLETE services={_services.Count} characteristics={characteristicCount}");
            Log($"FF31 present: {(ff31 ? "YES" : "NO")}");
            Log($"FF32 present: {(ff32 ? "YES" : "NO")}");
            if (ffe1 && _autoGatt?.NotifyCharacteristic != null && BleUuid.Is(_autoGatt.NotifyCharacteristic.Uuid, "FFE1"))
                Log("FFE1 present: YES (from Auto Detect cache)");
            else
                Log($"FFE1 present: {(ffe1 ? "YES" : "NO")}");
            UpdateStatePanel();
        }
        catch (Exception ex)
        {
            GattStateText.Text = "GATT: ERROR";
            Log($"*** GATT DISCOVERY FAILED: {ex.Message}");
            MessageBox.Show(this, ex.Message, "GATT discovery failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (gateHeld)
                _gattOperationGate.Release();
            RefreshButton.IsEnabled = true;
        }
    }

    private async Task<int> AddAutoDetectedServiceAsync()
    {
        if (_autoGatt?.IsAvailable != true)
            return 0;

        GattDeviceService service = _autoGatt.Service!;
        var serviceInfo = new GattServiceInfo
        {
            Service = service,
            Source = "AUTO-DETECT/REUSED",
            Reused = true,
            OwnsService = false,
            DiscoveryStatus = "Reused"
        };
        _services.Add(serviceInfo);
        var serviceItem = new TreeViewItem { Header = serviceInfo.Display, Tag = serviceInfo, IsExpanded = true };
        GattTree.Items.Add(serviceItem);

        Log($"SERVICE {BleUuid.Short(service.Uuid)} [{BleUuid.Full(service.Uuid)}]");
        Log("  SOURCE=AUTO-DETECT/REUSED");
        Log("*** USING EXISTING AUTO-DETECT GATT OBJECTS");

        var cachedCharacteristics = new List<GattCharacteristic>(_autoGatt.ServiceCharacteristics);
        if (_autoGatt.WriteCharacteristic != null && !cachedCharacteristics.Any(c => ReferenceEquals(c, _autoGatt.WriteCharacteristic) || c.Uuid == _autoGatt.WriteCharacteristic.Uuid))
            cachedCharacteristics.Add(_autoGatt.WriteCharacteristic);
        if (_autoGatt.NotifyCharacteristic != null && !cachedCharacteristics.Any(c => ReferenceEquals(c, _autoGatt.NotifyCharacteristic) || c.Uuid == _autoGatt.NotifyCharacteristic.Uuid))
            cachedCharacteristics.Add(_autoGatt.NotifyCharacteristic);

        foreach (GattCharacteristic characteristic in cachedCharacteristics)
        {
            GattCharacteristicInfo info = await AddCharacteristicAsync(
                serviceInfo,
                serviceItem,
                characteristic,
                "AUTO-DETECT/REUSED",
                reused: true);

            if (_autoGatt.CccdEnabled && ReferenceEquals(characteristic, _autoGatt.NotifyCharacteristic))
            {
                if (!_subscribedCharacteristics.Contains(characteristic))
                {
                    characteristic.ValueChanged += InspectorCharacteristic_ValueChanged;
                    _subscribedCharacteristics.Add(characteristic);
                    _kissDecoders.TryAdd(characteristic, new KissStreamDecoder());
                }
                info.NotifyEnabled = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify);
                info.IndicateEnabled = !info.NotifyEnabled && characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate);
                Log($"*** AUTO NOTIFY REUSED uuid={BleUuid.Short(characteristic.Uuid)} inspectorHandler=ATTACHED cccdWrite=SKIPPED existingCccdActive=true");
            }
        }

        if (_autoGatt.NotifyCharacteristic != null && BleUuid.Is(service.Uuid, "FFE0") && BleUuid.Is(_autoGatt.NotifyCharacteristic.Uuid, "FFE1"))
        {
            Log("*** FFE0 ENUMERATION SKIPPED TO AVOID SECOND GetCharacteristicsAsync");
            Log("*** FFE1 STILL AVAILABLE");
        }

        return serviceInfo.Characteristics.Count;
    }

    private async Task<GattServiceInfo> AddEnumeratedServiceAsync(GattDeviceService service)
    {
        var serviceInfo = new GattServiceInfo
        {
            Service = service,
            Source = "DISCOVERED",
            Reused = false,
            OwnsService = true
        };
        _services.Add(serviceInfo);
        var serviceItem = new TreeViewItem { Header = serviceInfo.Display, Tag = serviceInfo, IsExpanded = true };
        GattTree.Items.Add(serviceItem);

        Log($"SERVICE {BleUuid.Short(service.Uuid)} [{BleUuid.Full(service.Uuid)}]");

        GattCharacteristicsResult charsResult;
        try
        {
            charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        }
        catch (Exception ex)
        {
            serviceInfo.DiscoveryStatus = $"Exception 0x{ex.HResult:X8}";
            Log($"  CHARACTERISTICS ERROR service={BleUuid.Short(service.Uuid)} HRESULT=0x{ex.HResult:X8} exception={ex.Message}");
            return serviceInfo;
        }

        serviceInfo.DiscoveryStatus = charsResult.Status.ToString();
        Log($"  GetCharacteristicsAsync {StatusText(charsResult.Status, charsResult.ProtocolError)}");
        if (charsResult.Status != GattCommunicationStatus.Success)
        {
            if (charsResult.Status == GattCommunicationStatus.AccessDenied &&
                _autoGatt?.IsAvailable == true && service.Uuid == _autoGatt.Service!.Uuid)
            {
                Log("*** FFE0 ENUMERATION ACCESS DENIED");
                Log("*** USING EXISTING AUTO-DETECT GATT OBJECTS");
                Log("*** FFE1 STILL AVAILABLE");
                serviceInfo.DiscoveryStatus = "AccessDeniedButCached";
            }
            return serviceInfo;
        }

        foreach (GattCharacteristic characteristic in charsResult.Characteristics)
            await AddCharacteristicAsync(serviceInfo, serviceItem, characteristic, "DISCOVERED", reused: false);

        return serviceInfo;
    }

    private async Task<GattCharacteristicInfo> AddCharacteristicAsync(
        GattServiceInfo serviceInfo,
        TreeViewItem serviceItem,
        GattCharacteristic characteristic,
        string source,
        bool reused)
    {
        bool cccdKnownActive = reused && _autoGatt?.CccdEnabled == true && ReferenceEquals(characteristic, _autoGatt.NotifyCharacteristic);
        var charInfo = new GattCharacteristicInfo
        {
            Service = serviceInfo.Service,
            Characteristic = characteristic,
            Source = source,
            Reused = reused,
            CccdKnownActive = cccdKnownActive
        };
        serviceInfo.Characteristics.Add(charInfo);
        var charItem = new TreeViewItem { Header = charInfo.Display, Tag = charInfo };
        serviceItem.Items.Add(charItem);

        Log($"  CHARACTERISTIC {BleUuid.Short(characteristic.Uuid)} [{BleUuid.Full(characteristic.Uuid)}]");
        Log($"    SOURCE={source}");
        foreach (string property in GetEnabledPropertyNames(characteristic.CharacteristicProperties))
            Log($"    {property}");

        try
        {
            GattDescriptorsResult descriptorsResult = await characteristic.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
            Log($"    GetDescriptorsAsync {StatusText(descriptorsResult.Status, descriptorsResult.ProtocolError)}");
            if (descriptorsResult.Status == GattCommunicationStatus.Success)
            {
                foreach (GattDescriptor descriptor in descriptorsResult.Descriptors)
                {
                    charInfo.Descriptors.Add(descriptor);
                    string descriptorText = $"Descriptor: {BleUuid.Display(descriptor.Uuid)}";
                    charItem.Items.Add(new TreeViewItem { Header = descriptorText, Tag = descriptor });
                    Log($"    DESCRIPTOR {BleUuid.Short(descriptor.Uuid)} [{BleUuid.Full(descriptor.Uuid)}]");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"    DESCRIPTOR ERROR HRESULT=0x{ex.HResult:X8} exception={ex.Message}");
        }

        bool cccd = charInfo.Descriptors.Any(d => BleUuid.Is(d.Uuid, "2902")) || charInfo.CccdKnownActive;
        string cccdSource = charInfo.CccdKnownActive && !charInfo.Descriptors.Any(d => BleUuid.Is(d.Uuid, "2902"))
            ? " (ACTIVE VIA AUTO-DETECT CACHE)"
            : string.Empty;
        Log($"    CCCD={(cccd ? "YES" : "NO/NOT_ENUMERATED")}{cccdSource}");
        return charInfo;
    }

    private static void UpdatePresenceFromService(GattServiceInfo serviceInfo, ref bool ff31, ref bool ff32, ref bool ffe1)
    {
        foreach (GattCharacteristicInfo characteristic in serviceInfo.Characteristics)
        {
            ff31 |= BleUuid.Is(characteristic.Characteristic.Uuid, "FF31");
            ff32 |= BleUuid.Is(characteristic.Characteristic.Uuid, "FF32");
            ffe1 |= BleUuid.Is(characteristic.Characteristic.Uuid, "FFE1");
        }
    }

    private async Task OpenGattSessionAsync()
    {
        try
        {
            if (_session != null)
            {
                _session.MaxPduSizeChanged -= Session_MaxPduSizeChanged;
                _session.Dispose();
            }

            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            if (_session == null)
            {
                Log("*** MTU/PDU unavailable: GattSession could not be opened");
                return;
            }

            _session.MaintainConnection = true;
            _session.MaxPduSizeChanged += Session_MaxPduSizeChanged;
            _maxPduSize = _session.MaxPduSize;
            Log($"*** MTU/PDU={_maxPduSize} (Windows-managed; explicit requestMtu is not exposed by WinRT)");
            UpdateStatePanel();
        }
        catch (Exception ex)
        {
            _maxPduSize = null;
            Log($"*** MTU/PDU query failed (non-fatal): {ex.Message}");
        }
    }

    private void Session_MaxPduSizeChanged(GattSession sender, object args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _maxPduSize = sender.MaxPduSize;
            Log($"*** MTU/PDU CHANGED={_maxPduSize}");
            UpdateStatePanel();
        });
    }

    private void GattTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem item && item.Tag is GattCharacteristicInfo info)
            ShowCharacteristic(info);
    }

    private void ShowCharacteristic(GattCharacteristicInfo info)
    {
        _selectedCharacteristic = info;
        GattCharacteristicProperties p = info.Characteristic.CharacteristicProperties;
        SelectedUuidText.Text = $"{BleUuid.Label(info.Characteristic.Uuid)}\n{BleUuid.Full(info.Characteristic.Uuid)}\nService: {BleUuid.Display(info.Service.Uuid)}\nSource: {info.Source}\nReused: {(info.Reused ? "Yes" : "No")}";

        PropBroadcast.IsChecked = p.HasFlag(GattCharacteristicProperties.Broadcast);
        PropRead.IsChecked = p.HasFlag(GattCharacteristicProperties.Read);
        PropWriteNoResponse.IsChecked = p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        PropWrite.IsChecked = p.HasFlag(GattCharacteristicProperties.Write);
        PropNotify.IsChecked = p.HasFlag(GattCharacteristicProperties.Notify);
        PropIndicate.IsChecked = p.HasFlag(GattCharacteristicProperties.Indicate);
        PropSignedWrite.IsChecked = p.HasFlag(GattCharacteristicProperties.AuthenticatedSignedWrites);
        PropExtended.IsChecked = p.HasFlag(GattCharacteristicProperties.ExtendedProperties);

        DescriptorList.Items.Clear();
        foreach (GattDescriptor descriptor in info.Descriptors)
            DescriptorList.Items.Add(BleUuid.Display(descriptor.Uuid));
        if (info.Descriptors.Count == 0 && info.CccdKnownActive)
            DescriptorList.Items.Add("2902 - CCCD (active via Auto Detect cache; descriptor not re-enumerated)");
        else if (info.Descriptors.Count == 0)
            DescriptorList.Items.Add("(none discovered)");

        bool canRead = p.HasFlag(GattCharacteristicProperties.Read);
        bool canNotify = p.HasFlag(GattCharacteristicProperties.Notify);
        bool canIndicate = p.HasFlag(GattCharacteristicProperties.Indicate);
        bool canWrite = p.HasFlag(GattCharacteristicProperties.Write);
        bool canWriteNoResponse = p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);

        ReadButton.IsEnabled = canRead;
        EnableNotifyButton.IsEnabled = canNotify && !info.NotifyEnabled;
        DisableNotifyButton.IsEnabled = canNotify && info.NotifyEnabled;
        EnableIndicateButton.IsEnabled = canIndicate && !info.IndicateEnabled;
        DisableIndicateButton.IsEnabled = canIndicate && info.IndicateEnabled;
        WriteButton.IsEnabled = canWrite || canWriteNoResponse;

        ((ComboBoxItem)WriteTypeCombo.Items[0]).IsEnabled = canWrite;
        ((ComboBoxItem)WriteTypeCombo.Items[1]).IsEnabled = canWriteNoResponse;
        if (canWrite)
            WriteTypeCombo.SelectedIndex = 0;
        else if (canWriteNoResponse)
            WriteTypeCombo.SelectedIndex = 1;

        Rt950BaudButton.Visibility = BleUuid.Is(info.Characteristic.Uuid, "FF31") ? Visibility.Visible : Visibility.Collapsed;
        Rt950BaudButton.IsEnabled = canWrite || canWriteNoResponse;
    }

    private void ClearSelectionDetails()
    {
        _selectedCharacteristic = null;
        SelectedUuidText.Text = "Select a characteristic from the tree.";
        DescriptorList.Items.Clear();
        foreach (CheckBox box in new[] { PropBroadcast, PropRead, PropWriteNoResponse, PropWrite, PropNotify, PropIndicate, PropSignedWrite, PropExtended })
            box.IsChecked = false;
        ReadButton.IsEnabled = false;
        EnableNotifyButton.IsEnabled = false;
        DisableNotifyButton.IsEnabled = false;
        EnableIndicateButton.IsEnabled = false;
        DisableIndicateButton.IsEnabled = false;
        WriteButton.IsEnabled = false;
        Rt950BaudButton.Visibility = Visibility.Collapsed;
    }

    private async void EnableNotifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCharacteristic != null)
            await SetSubscriptionAsync(_selectedCharacteristic, GattClientCharacteristicConfigurationDescriptorValue.Notify);
    }

    private async void EnableIndicateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCharacteristic != null)
            await SetSubscriptionAsync(_selectedCharacteristic, GattClientCharacteristicConfigurationDescriptorValue.Indicate);
    }

    private async void DisableNotifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCharacteristic != null)
            await SetSubscriptionAsync(_selectedCharacteristic, GattClientCharacteristicConfigurationDescriptorValue.None);
    }

    private async void DisableIndicateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCharacteristic != null)
            await SetSubscriptionAsync(_selectedCharacteristic, GattClientCharacteristicConfigurationDescriptorValue.None);
    }

    private async Task SetSubscriptionAsync(GattCharacteristicInfo info, GattClientCharacteristicConfigurationDescriptorValue value)
    {
        await _gattOperationGate.WaitAsync();
        try
        {
            GattCharacteristic characteristic = info.Characteristic;
            bool protectedAutoTerminal = IsProtectedAutoTerminalCharacteristic(info);

            if (value == GattClientCharacteristicConfigurationDescriptorValue.None && protectedAutoTerminal)
            {
                characteristic.ValueChanged -= InspectorCharacteristic_ValueChanged;
                _subscribedCharacteristics.Remove(characteristic);
                _kissDecoders.Remove(characteristic);
                info.NotifyEnabled = false;
                info.IndicateEnabled = false;
                Log($"*** INSPECTOR SUBSCRIPTION DISABLED uuid={BleUuid.Full(characteristic.Uuid)}; Auto Detect terminal CCCD preserved");
                return;
            }

            if (value != GattClientCharacteristicConfigurationDescriptorValue.None &&
                protectedAutoTerminal &&
                _autoGatt?.CccdEnabled == true)
            {
                if (!_subscribedCharacteristics.Contains(characteristic))
                {
                    characteristic.ValueChanged += InspectorCharacteristic_ValueChanged;
                    _subscribedCharacteristics.Add(characteristic);
                    _kissDecoders.TryAdd(characteristic, new KissStreamDecoder());
                }

                info.NotifyEnabled = value == GattClientCharacteristicConfigurationDescriptorValue.Notify;
                info.IndicateEnabled = value == GattClientCharacteristicConfigurationDescriptorValue.Indicate;
                info.CccdKnownActive = true;
                Log($"*** AUTO CCCD REUSED uuid={BleUuid.Short(characteristic.Uuid)} source=AUTO-DETECT/REUSED descriptorWrite=SKIPPED existingSubscription=ACTIVE");
                return;
            }

            if (value != GattClientCharacteristicConfigurationDescriptorValue.None && !_subscribedCharacteristics.Contains(characteristic))
            {
                characteristic.ValueChanged += InspectorCharacteristic_ValueChanged;
                _subscribedCharacteristics.Add(characteristic);
                _kissDecoders.TryAdd(characteristic, new KissStreamDecoder());
            }

            Log($"*** {BleUuid.Short(characteristic.Uuid)} CCCD WRITE START source={info.Source}");
            GattWriteResult result = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(value);
            string op = value switch
            {
                GattClientCharacteristicConfigurationDescriptorValue.Notify => "NOTIFY",
                GattClientCharacteristicConfigurationDescriptorValue.Indicate => "INDICATE",
                _ => "SUBSCRIPTION DISABLE"
            };
            Log($"onDescriptorWrite uuid=2902 char={BleUuid.Short(characteristic.Uuid)} {StatusText(result.Status, result.ProtocolError)} source={info.Source}");
            Log($"*** {BleUuid.Short(characteristic.Uuid)} CCCD WRITE RESULT={result.Status} statusCode={(int)result.Status} protocol={ProtocolText(result.ProtocolError)}");

            if (result.Status == GattCommunicationStatus.Success)
            {
                info.NotifyEnabled = value == GattClientCharacteristicConfigurationDescriptorValue.Notify;
                info.IndicateEnabled = value == GattClientCharacteristicConfigurationDescriptorValue.Indicate;

                if (protectedAutoTerminal && _autoGatt != null)
                {
                    _autoGatt.CccdEnabled = value != GattClientCharacteristicConfigurationDescriptorValue.None;
                    if (_autoGatt.CccdEnabled)
                        info.CccdKnownActive = true;
                    AutoTerminalCccdStateChanged?.Invoke(_autoGatt, _autoGatt.CccdEnabled);
                }

                if (value == GattClientCharacteristicConfigurationDescriptorValue.None)
                {
                    characteristic.ValueChanged -= InspectorCharacteristic_ValueChanged;
                    _subscribedCharacteristics.Remove(characteristic);
                    _kissDecoders.Remove(characteristic);
                    Log($"*** SUBSCRIPTION DISABLED uuid={BleUuid.Full(characteristic.Uuid)}");
                }
                else
                {
                    Log($"*** {op} ENABLED uuid={BleUuid.Full(characteristic.Uuid)}");
                }
            }
            else
            {
                LogSubscriptionFailure(info, result.Status, result.ProtocolError, null);
                if (protectedAutoTerminal)
                    AutoTerminalCccdStateChanged?.Invoke(_autoGatt!, false);
                if (!info.NotifyEnabled && !info.IndicateEnabled)
                {
                    characteristic.ValueChanged -= InspectorCharacteristic_ValueChanged;
                    _subscribedCharacteristics.Remove(characteristic);
                    _kissDecoders.Remove(characteristic);
                }
            }
        }
        catch (Exception ex)
        {
            LogSubscriptionFailure(info, null, null, ex);
            if (IsProtectedAutoTerminalCharacteristic(info))
                AutoTerminalCccdStateChanged?.Invoke(_autoGatt!, false);
            if (!info.NotifyEnabled && !info.IndicateEnabled)
            {
                try { info.Characteristic.ValueChanged -= InspectorCharacteristic_ValueChanged; } catch { }
                _subscribedCharacteristics.Remove(info.Characteristic);
                _kissDecoders.Remove(info.Characteristic);
            }
        }
        finally
        {
            _gattOperationGate.Release();
            ShowCharacteristic(info);
            UpdateStatePanel();
        }
    }

    private void LogSubscriptionFailure(
        GattCharacteristicInfo info,
        GattCommunicationStatus? status,
        byte? protocolError,
        Exception? exception)
    {
        string sessionStatus = "unknown";
        try { sessionStatus = _session?.SessionStatus.ToString() ?? info.Service.Session?.SessionStatus.ToString() ?? "unknown"; } catch { }

        Log($"*** {BleUuid.Short(info.Characteristic.Uuid)} CCCD FAILED");
        Log($"status={(status?.ToString() ?? "EXCEPTION")} statusCode={(status.HasValue ? ((int)status.Value).ToString(CultureInfo.InvariantCulture) : "n/a")} protocol={ProtocolText(protocolError)}");
        Log($"service={BleUuid.Full(info.Service.Uuid)}");
        Log($"serviceSource={info.Source}");
        Log($"characteristicSource={info.Source}");
        Log($"cachedReused={info.Reused.ToString().ToLowerInvariant()}");
        Log($"connectionState={_device.ConnectionStatus}");
        Log($"gattSessionStatus={sessionStatus}");
        if (exception != null)
        {
            Log($"HRESULT=0x{exception.HResult:X8}");
            Log($"exception={exception.Message}");
        }
    }

    private async Task DisableAllSubscriptionsBestEffortAsync()
    {
        if (_subscribedCharacteristics.Count == 0) return;

        await _gattOperationGate.WaitAsync();
        try
        {
            foreach (GattCharacteristic characteristic in _subscribedCharacteristics.ToArray())
            {
                try
                {
                    GattCharacteristicInfo? info = _services.SelectMany(s => s.Characteristics)
                        .FirstOrDefault(i => ReferenceEquals(i.Characteristic, characteristic));
                    if (info == null || !IsProtectedAutoTerminalCharacteristic(info))
                    {
                        await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None);
                    }
                    else
                    {
                        Log($"*** PRESERVE AUTO TERMINAL CCCD uuid={BleUuid.Short(characteristic.Uuid)}");
                    }
                }
                catch { }
                try { characteristic.ValueChanged -= InspectorCharacteristic_ValueChanged; } catch { }
            }
            _subscribedCharacteristics.Clear();
            _kissDecoders.Clear();
            foreach (GattCharacteristicInfo info in _services.SelectMany(s => s.Characteristics))
            {
                info.NotifyEnabled = false;
                info.IndicateEnabled = false;
            }
        }
        finally
        {
            _gattOperationGate.Release();
            UpdateStatePanel();
        }
    }

    private void InspectorCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            byte[] data = BufferToBytes(args.CharacteristicValue);
            Interlocked.Add(ref _rxBytes, data.Length);
            _dataReceived = true;

            Dispatcher.BeginInvoke(() =>
            {
                Log("*** RAW BLE NOTIFICATION");
                Log($"UUID={BleUuid.Short(sender.Uuid)} [{BleUuid.Full(sender.Uuid)}]");
                Log($"LEN={data.Length}");
                Log($"HEX={Hex(data)}");
                LogRx("RX NOTIFY", sender, data);
                if (_kissDecoders.TryGetValue(sender, out KissStreamDecoder? decoder))
                {
                    foreach (KissFrame frame in decoder.Push(data))
                        LogKissFrame(sender, frame);
                }
                UpdateStatePanel();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() => Log($"*** RX NOTIFY ERROR uuid={BleUuid.Short(sender.Uuid)} exception={ex.Message}"));
        }
    }

    private async void ReadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCharacteristic == null) return;
        GattCharacteristic characteristic = _selectedCharacteristic.Characteristic;

        await _gattOperationGate.WaitAsync();
        try
        {
            GattReadResult result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            Log($"onCharacteristicRead uuid={BleUuid.Short(characteristic.Uuid)} {StatusText(result.Status, result.ProtocolError)}");
            if (result.Status == GattCommunicationStatus.Success && result.Value != null)
            {
                byte[] data = BufferToBytes(result.Value);
                Interlocked.Add(ref _rxBytes, data.Length);
                _dataReceived = true;
                LogRx("READ", characteristic, data);
                UpdateStatePanel();
            }
            else
            {
                Log($"*** READ FAILED uuid={BleUuid.Full(characteristic.Uuid)} {StatusText(result.Status, result.ProtocolError)}");
            }
        }
        catch (Exception ex)
        {
            Log($"*** READ ERROR uuid={BleUuid.Full(characteristic.Uuid)} exception={ex.Message}");
        }
        finally
        {
            _gattOperationGate.Release();
        }
    }

    private async void WriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCharacteristic == null) return;
        byte[] payload;
        try
        {
            payload = ParseWritePayload(WriteInputText.Text, WriteModeCombo.SelectedIndex);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid write data", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (payload.Length == 0) return;
        GattWriteOption option = WriteTypeCombo.SelectedIndex == 1
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
        await WriteCharacteristicAsync(_selectedCharacteristic, payload, option, "MANUAL WRITE");
    }

    private async void Rt950BaudButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCharacteristic == null || !BleUuid.Is(_selectedCharacteristic.Characteristic.Uuid, "FF31"))
            return;

        GattCharacteristicProperties p = _selectedCharacteristic.Characteristic.CharacteristicProperties;
        GattWriteOption option = p.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;
        byte[] payload = Encoding.ASCII.GetBytes("AT+BAUD?");
        await WriteCharacteristicAsync(_selectedCharacteristic, payload, option, "RT-950 AT+BAUD?");
    }

    private async Task WriteCharacteristicAsync(GattCharacteristicInfo info, byte[] payload, GattWriteOption option, string label)
    {
        GattCharacteristic characteristic = info.Characteristic;
        GattCharacteristicProperties p = characteristic.CharacteristicProperties;
        if (option == GattWriteOption.WriteWithResponse && !p.HasFlag(GattCharacteristicProperties.Write))
        {
            Log($"*** WRITE BLOCKED uuid={BleUuid.Short(characteristic.Uuid)} reason=Write With Response unsupported");
            return;
        }
        if (option == GattWriteOption.WriteWithoutResponse && !p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
        {
            Log($"*** WRITE BLOCKED uuid={BleUuid.Short(characteristic.Uuid)} reason=Write Without Response unsupported");
            return;
        }

        await _gattOperationGate.WaitAsync();
        try
        {
            int chunkSize = _maxPduSize.HasValue && _maxPduSize.Value > 3
                ? Math.Max(1, (int)_maxPduSize.Value - 3)
                : 20;

            for (int offset = 0; offset < payload.Length; offset += chunkSize)
            {
                int count = Math.Min(chunkSize, payload.Length - offset);
                byte[] chunk = new byte[count];
                System.Buffer.BlockCopy(payload, offset, chunk, 0, count);
                using var writer = new DataWriter();
                writer.WriteBytes(chunk);
                IBuffer buffer = writer.DetachBuffer();

                GattWriteResult result = await characteristic.WriteValueWithResultAsync(buffer, option);
                Log($"onCharacteristicWrite uuid={BleUuid.Short(characteristic.Uuid)} {StatusText(result.Status, result.ProtocolError)} offset={offset} len={count}");
                if (result.Status != GattCommunicationStatus.Success)
                {
                    Log($"*** WRITE FAILED uuid={BleUuid.Full(characteristic.Uuid)} {StatusText(result.Status, result.ProtocolError)}");
                    return;
                }
            }

            Log($"*** {label} OK uuid={BleUuid.Short(characteristic.Uuid)} len={payload.Length} option={option}");
            Log($"TX HEX: {Hex(payload)}");
            Log($"TX ASCII: {PrintableAscii(payload)}");
        }
        catch (Exception ex)
        {
            Log($"*** WRITE ERROR uuid={BleUuid.Full(characteristic.Uuid)} exception={ex.Message}");
        }
        finally
        {
            _gattOperationGate.Release();
        }
    }

    private void LogRx(string operation, GattCharacteristic characteristic, byte[] data)
    {
        Log($"[{DateTime.Now:HH:mm:ss.fff}] {operation}");
        Log($"UUID: {BleUuid.Short(characteristic.Uuid)} [{BleUuid.Full(characteristic.Uuid)}]");
        Log($"LEN: {data.Length}");
        Log($"HEX: {Hex(data)}");
        Log($"ASCII: {PrintableAscii(data)}");
    }

    private void LogKissFrame(GattCharacteristic characteristic, KissFrame frame)
    {
        string cmd = frame.PortCommand.HasValue ? frame.PortCommand.Value.ToString("X2") : "--";
        Log("*** KISS FRAME DETECTED");
        Log($"SOURCE: {BleUuid.Short(characteristic.Uuid)}");
        Log($"LEN: {frame.Payload.Length}");
        Log($"PORT/CMD: {cmd}");
        Log($"RAW HEX: {Hex(frame.Raw)}");
        Log($"UNESCAPED: {Hex(frame.Payload)}");
    }

    private void Log(string message)
    {
        string line = message.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? message : message + Environment.NewLine;
        _debugLog.Append(line);
        InspectorLogTextBox.AppendText(line);
        InspectorLogTextBox.ScrollToEnd();
    }

    private void ExportLogButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export GATT debug log",
            Filter = "Text files (*.txt)|*.txt|Log files (*.log)|*.log|All files (*.*)|*.*",
            FileName = $"BLE_GATT_{SanitizeFileName(_deviceName)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != true) return;

        var export = new StringBuilder();
        export.AppendLine("BLE Serial Terminal - GATT Inspector Debug Export");
        export.AppendLine($"Device name: {_deviceName}");
        export.AppendLine($"MAC address: {FormatBluetoothAddress(_address)}");
        export.AppendLine($"Windows version: {Environment.OSVersion.VersionString}");
        export.AppendLine($"Connection timestamp: {_connectionTimestamp:O}");
        export.AppendLine($"Negotiated MTU/PDU: {(_maxPduSize?.ToString(CultureInfo.InvariantCulture) ?? "OS-managed / unavailable")}");
        export.AppendLine();
        export.AppendLine("===== DISCOVERED GATT =====");
        foreach (GattServiceInfo service in _services)
        {
            export.AppendLine($"SERVICE {BleUuid.Short(service.Service.Uuid)} {BleUuid.Full(service.Service.Uuid)} SOURCE={service.Source} REUSED={service.Reused} DISCOVERY={service.DiscoveryStatus}");
            foreach (GattCharacteristicInfo ch in service.Characteristics)
            {
                export.AppendLine($"  CHARACTERISTIC {BleUuid.Short(ch.Characteristic.Uuid)} {BleUuid.Full(ch.Characteristic.Uuid)} SOURCE={ch.Source} REUSED={ch.Reused}");
                export.AppendLine($"    PROPERTIES: {string.Join(", ", GetEnabledPropertyNames(ch.Characteristic.CharacteristicProperties))}");
                export.AppendLine($"    NOTIFY_ENABLED={ch.NotifyEnabled} INDICATE_ENABLED={ch.IndicateEnabled}");
                foreach (GattDescriptor descriptor in ch.Descriptors)
                    export.AppendLine($"    DESCRIPTOR {BleUuid.Short(descriptor.Uuid)} {BleUuid.Full(descriptor.Uuid)}");
            }
        }
        export.AppendLine();
        export.AppendLine("===== EVENT LOG =====");
        export.Append(_debugLog);

        System.IO.File.WriteAllText(dialog.FileName, export.ToString(), new UTF8Encoding(false));
        Log($"*** LOG EXPORTED {dialog.FileName}");
    }

    private void UpdateStatePanel()
    {
        BleStateText.Text = $"BLE: {_device.ConnectionStatus.ToString().ToUpperInvariant()}";
        NotifyStateText.Text = $"Notify/Indicate: {_subscribedCharacteristics.Count} ON";
        DataStateText.Text = $"Data: {(_dataReceived ? "RECEIVED" : "NO")}";
        MtuStateText.Text = _maxPduSize.HasValue ? $"MTU/PDU: {_maxPduSize}" : "MTU/PDU: OS-managed";
        RxBytesText.Text = $"RX Bytes: {Interlocked.Read(ref _rxBytes)}";

        bool ffe1Found = _services.SelectMany(s => s.Characteristics).Any(c => BleUuid.Is(c.Characteristic.Uuid, "FFE1")) ||
                         (_autoGatt?.NotifyCharacteristic != null && BleUuid.Is(_autoGatt.NotifyCharacteristic.Uuid, "FFE1"));
        bool handlerAttached = _autoGatt?.NotifyCharacteristic != null && BleUuid.Is(_autoGatt.NotifyCharacteristic.Uuid, "FFE1")
            ? _autoGatt.NotifyHandlerAttached
            : false;
        bool cccdEnabled = _autoGatt?.NotifyCharacteristic != null && BleUuid.Is(_autoGatt.NotifyCharacteristic.Uuid, "FFE1")
            ? _autoGatt.CccdEnabled
            : false;
        bool rt950 = _autoGatt?.IsRadtelRt950Kiss == true;

        Ffe1StateText.Text = $"FFE1: {(ffe1Found ? "FOUND" : "NOT FOUND")}";
        Ffe1HandlerStateText.Text = $"FFE1 Handler: {(handlerAttached ? "YES" : "NO")}";
        Ffe1CccdStateText.Text = $"FFE1 CCCD: {(cccdEnabled ? "SUCCESS" : "NOT READY")}";
        Rt950StateText.Text = $"RT950 KISS: {(rt950 && cccdEnabled ? "READY" : "NOT READY")}";
    }

    private bool IsProtectedAutoTerminalCharacteristic(GattCharacteristicInfo info)
    {
        if (_autoGatt?.NotifyCharacteristic == null || _autoGatt.Service == null)
            return false;

        return ReferenceEquals(info.Characteristic, _autoGatt.NotifyCharacteristic) ||
               (info.Service.Uuid == _autoGatt.Service.Uuid && info.Characteristic.Uuid == _autoGatt.NotifyCharacteristic.Uuid);
    }

    private void DisposeDiscoveredServices()
    {
        foreach (GattServiceInfo service in _services)
        {
            if (!service.OwnsService)
                continue;
            try { service.Service.Dispose(); } catch { }
        }
        _services.Clear();
    }

    private static byte[] ParseWritePayload(string input, int mode)
    {
        return mode switch
        {
            1 => ParseHex(input),
            2 => Encoding.UTF8.GetBytes(input),
            _ => Encoding.ASCII.GetBytes(input)
        };
    }

    private static byte[] ParseHex(string input)
    {
        string compact = new string(input.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != ':' && c != ',').ToArray());
        if (compact.Length == 0) return Array.Empty<byte>();
        if ((compact.Length & 1) != 0)
            throw new FormatException("HEX data must contain an even number of hex digits.");

        byte[] bytes = new byte[compact.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            string pair = compact.Substring(i * 2, 2);
            if (!byte.TryParse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                throw new FormatException($"Invalid HEX byte: {pair}");
        }
        return bytes;
    }

    private static byte[] BufferToBytes(IBuffer buffer)
    {
        using DataReader reader = DataReader.FromBuffer(buffer);
        byte[] data = new byte[checked((int)reader.UnconsumedBufferLength)];
        reader.ReadBytes(data);
        return data;
    }

    private static string PrintableAscii(byte[] data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (byte b in data)
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
        return sb.ToString();
    }

    private static string Hex(byte[] data) => BitConverter.ToString(data).Replace('-', ' ');

    private static IEnumerable<string> GetEnabledPropertyNames(GattCharacteristicProperties p)
    {
        if (p.HasFlag(GattCharacteristicProperties.Broadcast)) yield return "BROADCAST";
        if (p.HasFlag(GattCharacteristicProperties.Read)) yield return "READ";
        if (p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)) yield return "WRITE_NO_RESPONSE";
        if (p.HasFlag(GattCharacteristicProperties.Write)) yield return "WRITE";
        if (p.HasFlag(GattCharacteristicProperties.Notify)) yield return "NOTIFY";
        if (p.HasFlag(GattCharacteristicProperties.Indicate)) yield return "INDICATE";
        if (p.HasFlag(GattCharacteristicProperties.AuthenticatedSignedWrites)) yield return "AUTHENTICATED_SIGNED_WRITES";
        if (p.HasFlag(GattCharacteristicProperties.ExtendedProperties)) yield return "EXTENDED_PROPERTIES";
    }

    private static string ProtocolText(byte? protocolError) => protocolError.HasValue ? $"0x{protocolError.Value:X2}" : "none";

    private static string StatusText(GattCommunicationStatus status, byte? protocolError)
        => $"status={status} statusCode={(int)status} protocol={ProtocolText(protocolError)}";

    private static string FormatBluetoothAddress(ulong address)
    {
        byte[] bytes = BitConverter.GetBytes(address);
        return string.Join(":", bytes.Take(6).Reverse().Select(b => b.ToString("X2")));
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            bool dark = true;
            if (Application.Current.Resources["WindowBackgroundBrush"] is SolidColorBrush brush)
            {
                Color c = brush.Color;
                dark = (c.R + c.G + c.B) < 384;
            }
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int enabled = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private static string SanitizeFileName(string name)
    {
        foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "BLE_Device" : name;
    }
}
