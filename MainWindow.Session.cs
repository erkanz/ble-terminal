using System.Reflection;
using System.Windows;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private SessionCaptureWindow? _sessionCaptureWindow;

    private void SessionCaptureMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionCaptureWindow is { IsLoaded: true })
        {
            _sessionCaptureWindow.Activate();
            return;
        }

        _sessionCaptureWindow = new SessionCaptureWindow(AppLogHub.Store, BuildSessionCaptureMetadata)
        {
            Owner = this
        };
        _sessionCaptureWindow.Closed += (_, _) => _sessionCaptureWindow = null;
        _sessionCaptureWindow.Show();
    }

    private void CloseSessionCaptureWindow()
    {
        try { _sessionCaptureWindow?.Close(); } catch { }
        _sessionCaptureWindow = null;
    }

    private SessionMetadata BuildSessionCaptureMetadata()
    {
        string serviceUuid = _service != null ? BleUuid.Full(_service.Uuid) : string.Empty;
        string writeUuid = _writeCharacteristic != null ? BleUuid.Full(_writeCharacteristic.Uuid) : string.Empty;
        string notifyUuid = _notifyCharacteristic != null ? BleUuid.Full(_notifyCharacteristic.Uuid) : string.Empty;
        string profile = _autoGatt.IsAvailable
            ? _autoGatt.ProfileName
            : _writeCharacteristic != null || _notifyCharacteristic != null
                ? "Custom / Manual GATT"
                : string.Empty;

        var metadata = new SessionMetadata
        {
            ApplicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            Device = CurrentLogDevice(),
            Address = _connectedAddress.HasValue ? FormatBluetoothAddress(_connectedAddress.Value) : string.Empty,
            ConnectionState = _device?.ConnectionStatus.ToString() ?? "Disconnected",
            Profile = profile,
            ServiceUuid = serviceUuid,
            WriteUuid = writeUuid,
            NotifyUuid = notifyUuid,
            SameCharacteristic = _writeCharacteristic != null && _notifyCharacteristic != null &&
                                 ReferenceEquals(_writeCharacteristic, _notifyCharacteristic)
        };

        if (_autoGatt.Service != null && _autoGatt.ServiceCharacteristics.Count > 0)
        {
            // Preserve the complete Auto Detect service snapshot, but mark roles from the
            // CURRENT live route. This keeps capture metadata truthful after a manual TX
            // override such as Notify=FFE1 / Write=FF31 without mutating AutoDetectedGattContext.
            foreach (GattCharacteristic characteristic in _autoGatt.ServiceCharacteristics)
            {
                bool isWrite = _writeCharacteristic != null &&
                               (ReferenceEquals(characteristic, _writeCharacteristic) || characteristic.Uuid == _writeCharacteristic.Uuid);
                bool isNotify = _notifyCharacteristic != null &&
                                (ReferenceEquals(characteristic, _notifyCharacteristic) || characteristic.Uuid == _notifyCharacteristic.Uuid);
                string role = isWrite && isNotify ? "WRITE+NOTIFY" : isWrite ? "WRITE" : isNotify ? "NOTIFY" : "SERVICE";
                metadata.RelevantGatt.Add(new SessionGattCharacteristic
                {
                    Role = role,
                    ServiceUuid = BleUuid.Full(_autoGatt.Service.Uuid),
                    CharacteristicUuid = BleUuid.Full(characteristic.Uuid),
                    Properties = characteristic.CharacteristicProperties.ToString()
                });
            }
        }
        else
        {
            AddManualRelevantCharacteristic(metadata, "WRITE", _service, _writeCharacteristic);
            if (_notifyCharacteristic != null && !ReferenceEquals(_notifyCharacteristic, _writeCharacteristic))
                AddManualRelevantCharacteristic(metadata, "NOTIFY", _service, _notifyCharacteristic);
            else if (_notifyCharacteristic != null && metadata.RelevantGatt.Count > 0)
                metadata.RelevantGatt[0].Role = "WRITE+NOTIFY";
        }

        return metadata;
    }

    private static void AddManualRelevantCharacteristic(
        SessionMetadata metadata,
        string role,
        GattDeviceService? service,
        GattCharacteristic? characteristic)
    {
        if (characteristic == null)
            return;
        metadata.RelevantGatt.Add(new SessionGattCharacteristic
        {
            Role = role,
            ServiceUuid = service == null ? string.Empty : BleUuid.Full(service.Uuid),
            CharacteristicUuid = BleUuid.Full(characteristic.Uuid),
            Properties = characteristic.CharacteristicProperties.ToString()
        });
    }
}
