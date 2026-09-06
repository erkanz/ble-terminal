using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BLESerialTerminal;

public partial class GattInspectorWindow
{
    private GattSnapshotWindow? _snapshotWindow;

    static GattInspectorWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(GattInspectorWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(GattInspectorSnapshot_Loaded));
    }

    private static void GattInspectorSnapshot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not GattInspectorWindow window)
            return;
        window.InstallSnapshotCompareButton();
    }

    private void InstallSnapshotCompareButton()
    {
        if (RefreshButton.Parent is not StackPanel panel)
            return;
        if (panel.Children.OfType<Button>().Any(b => Equals(b.Tag, "GATT_SNAPSHOT_COMPARE")))
            return;

        var button = new Button
        {
            Content = "Snapshot / Compare",
            Width = 125,
            Margin = new Thickness(8, 0, 0, 0),
            Tag = "GATT_SNAPSHOT_COMPARE",
            ToolTip = "Capture, export, import and compare GATT snapshots without changing characteristic values."
        };
        button.Click += SnapshotCompareButton_Click;
        panel.Children.Insert(1, button);
    }

    private void SnapshotCompareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshotWindow is { IsLoaded: true })
        {
            _snapshotWindow.Activate();
            return;
        }

        _snapshotWindow = new GattSnapshotWindow(CaptureCurrentGattSnapshotAsync)
        {
            Owner = this
        };
        _snapshotWindow.Closed += (_, _) => _snapshotWindow = null;
        _snapshotWindow.Show();
    }

    private async Task<GattSnapshot> CaptureCurrentGattSnapshotAsync()
    {
        if (_closing)
            throw new InvalidOperationException("GATT Inspector is closing.");
        if (_device.ConnectionStatus.ToString().Equals("Disconnected", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The BLE device is disconnected. Load saved snapshots for offline comparison instead.");

        // Reuse the Inspector's qualified discovery path. It already serializes GATT operations,
        // reuses the protected Auto Detect service/characteristics, and avoids a second FFE0
        // GetCharacteristicsAsync on the RT-950 cache-owned service.
        await DiscoverGattAsync();
        return BuildGattSnapshotFromInspectorState();
    }

    private GattSnapshot BuildGattSnapshotFromInspectorState()
    {
        GattSnapshotRouteState route = (Owner as MainWindow)?.GetGattSnapshotRouteState() ?? BuildFallbackRouteState();
        string[] logLines = _debugLog.ToString()
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        string serviceDiscoveryStatus = logLines
            .LastOrDefault(line => line.Contains("GetGattServicesAsync", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

        var snapshot = new GattSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            Metadata = new GattSnapshotMetadata
            {
                ApplicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                DeviceName = _deviceName,
                Address = FormatBluetoothAddress(_address),
                ConnectionTimestamp = _connectionTimestamp,
                ConnectionState = _device.ConnectionStatus.ToString(),
                GattState = GattStateText.Text,
                ServiceDiscoveryStatus = serviceDiscoveryStatus,
                AutoProfile = route.AutoProfile,
                AutoServiceUuid = route.AutoServiceUuid,
                AutoWriteUuid = route.AutoWriteUuid,
                AutoNotifyUuid = route.AutoNotifyUuid,
                AutoSameCharacteristic = route.AutoSameCharacteristic,
                ManualServiceUuid = route.ManualServiceUuid,
                ManualWriteUuid = route.ManualWriteUuid,
                ManualNotifyUuid = route.ManualNotifyUuid,
                ManualWriteType = route.ManualWriteType,
                ManualOverrideActive = route.ManualOverrideActive
            }
        };

        foreach (GattServiceInfo serviceInfo in _services)
        {
            var service = new GattSnapshotService
            {
                ShortUuid = BleUuid.Short(serviceInfo.Service.Uuid),
                FullUuid = BleUuid.Full(serviceInfo.Service.Uuid),
                Source = serviceInfo.Source,
                Reused = serviceInfo.Reused,
                Ownership = serviceInfo.OwnsService ? "INSPECTOR/DISCOVERED" : "AUTO-DETECT/OWNER",
                DiscoveryStatus = serviceInfo.DiscoveryStatus
            };

            foreach (GattCharacteristicInfo characteristicInfo in serviceInfo.Characteristics)
            {
                GattCharacteristic characteristic = characteristicInfo.Characteristic;
                GattCharacteristicProperties p = characteristic.CharacteristicProperties;
                var item = new GattSnapshotCharacteristic
                {
                    ShortUuid = BleUuid.Short(characteristic.Uuid),
                    FullUuid = BleUuid.Full(characteristic.Uuid),
                    Properties = p.ToString(),
                    Source = characteristicInfo.Source,
                    Reused = characteristicInfo.Reused,
                    Ownership = serviceInfo.OwnsService ? "INSPECTOR/DISCOVERED" : "AUTO-DETECT/OWNER",
                    ReadableAdvertised = p.HasFlag(GattCharacteristicProperties.Read),
                    WritableWithResponseAdvertised = p.HasFlag(GattCharacteristicProperties.Write),
                    WritableWithoutResponseAdvertised = p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse),
                    NotifyAdvertised = p.HasFlag(GattCharacteristicProperties.Notify),
                    IndicateAdvertised = p.HasFlag(GattCharacteristicProperties.Indicate),
                    CccdKnownActive = characteristicInfo.CccdKnownActive || characteristicInfo.NotifyEnabled || characteristicInfo.IndicateEnabled,
                    DescriptorDiscoveryStatus = characteristicInfo.Descriptors.Count > 0
                        ? $"CAPTURED count={characteristicInfo.Descriptors.Count}"
                        : "NONE_OR_NOT_ENUMERATED; see discovery evidence"
                };

                foreach (GattDescriptor descriptor in characteristicInfo.Descriptors)
                {
                    item.Descriptors.Add(new GattSnapshotDescriptor
                    {
                        ShortUuid = BleUuid.Short(descriptor.Uuid),
                        FullUuid = BleUuid.Full(descriptor.Uuid),
                        Source = characteristicInfo.Source
                    });
                }

                service.Characteristics.Add(item);
            }

            snapshot.Services.Add(service);
        }

        snapshot.DiscoveryEvidence = logLines
            .Where(IsSnapshotDiscoveryEvidence)
            .TakeLast(250)
            .ToList();

        GattSnapshotSerializer.Normalize(snapshot);
        return snapshot;
    }

    private GattSnapshotRouteState BuildFallbackRouteState()
    {
        return new GattSnapshotRouteState
        {
            AutoProfile = _autoGatt?.ProfileName ?? string.Empty,
            AutoServiceUuid = _autoGatt?.Service == null ? string.Empty : BleUuid.Full(_autoGatt.Service.Uuid),
            AutoWriteUuid = _autoGatt?.WriteCharacteristic == null ? string.Empty : BleUuid.Full(_autoGatt.WriteCharacteristic.Uuid),
            AutoNotifyUuid = _autoGatt?.NotifyCharacteristic == null ? string.Empty : BleUuid.Full(_autoGatt.NotifyCharacteristic.Uuid),
            AutoSameCharacteristic = _autoGatt?.SameCharacteristic == true,
            ManualServiceUuid = _autoGatt?.Service == null ? string.Empty : BleUuid.Full(_autoGatt.Service.Uuid),
            ManualWriteUuid = _autoGatt?.WriteCharacteristic == null ? string.Empty : BleUuid.Full(_autoGatt.WriteCharacteristic.Uuid),
            ManualNotifyUuid = _autoGatt?.NotifyCharacteristic == null ? string.Empty : BleUuid.Full(_autoGatt.NotifyCharacteristic.Uuid),
            ManualWriteType = string.Empty,
            ManualOverrideActive = false
        };
    }

    private static bool IsSnapshotDiscoveryEvidence(string line)
    {
        return line.Contains("GetGattServicesAsync", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("GetCharacteristicsAsync", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("GetDescriptorsAsync", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("ACCESS DENIED", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("DISCOVERY FAILED", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("AUTO-DETECT/REUSED", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("USING EXISTING AUTO-DETECT GATT OBJECTS", StringComparison.OrdinalIgnoreCase);
    }
}
