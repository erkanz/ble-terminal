using System.Windows;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private const int MaxNotificationCaptureHistory = 10000;
    private readonly NotificationCaptureStore _notificationCaptureStore = new(MaxNotificationCaptureHistory);
    private NotificationMonitorWindow? _notificationMonitorWindow;
    private string _terminalNotificationMode = "Notify/Indicate";

    private void NotificationMonitorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_notificationMonitorWindow is { IsLoaded: true })
        {
            _notificationMonitorWindow.Activate();
            return;
        }

        _notificationMonitorWindow = new NotificationMonitorWindow(_notificationCaptureStore)
        {
            Owner = this
        };
        _notificationMonitorWindow.Closed += (_, _) => _notificationMonitorWindow = null;
        _notificationMonitorWindow.Show();
    }

    private NotificationRecord CaptureMainNotification(GattCharacteristic characteristic, byte[] data)
    {
        string deviceName = _device?.Name ?? "Unnamed BLE device";
        Guid? serviceUuid = _service?.Uuid;
        bool autoDetected = _autoGatt.IsAvailable && ReferenceEquals(_autoGatt.NotifyCharacteristic, characteristic);
        string source = autoDetected ? "AUTO-DETECT" : "MANUAL";

        return _notificationCaptureStore.Add(
            deviceName,
            serviceUuid,
            characteristic.Uuid,
            source,
            reused: false,
            _terminalNotificationMode,
            data);
    }

    private void CloseNotificationMonitorWindow()
    {
        try
        {
            _notificationMonitorWindow?.Close();
        }
        catch
        {
        }
        _notificationMonitorWindow = null;
    }
}
