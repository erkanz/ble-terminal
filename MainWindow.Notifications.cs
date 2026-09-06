using System.Windows;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private readonly NotificationCaptureStore _notificationCaptureStore = NotificationCaptureHub.Store;
    private NotificationMonitorWindow? _notificationMonitorWindow;
    private GattCharacteristic? _notificationCaptureCharacteristic;
    private KissStreamDecoder _notificationMonitorKissDecoder = new();
    private string _terminalNotificationMode = "Notify/Indicate";

    private void InitializeNotificationCaptureHooks()
    {
        GattInspectorButton.IsEnabledChanged += NotificationConnectionUiStateChanged;
        SyncMainNotificationCaptureHandler();
    }

    private void ShutdownNotificationCaptureHooks()
    {
        GattInspectorButton.IsEnabledChanged -= NotificationConnectionUiStateChanged;
        DetachMainNotificationCaptureHandler();
    }

    private void NotificationConnectionUiStateChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        SyncMainNotificationCaptureHandler();

    private void SyncMainNotificationCaptureHandler()
    {
        GattCharacteristic? target = GattInspectorButton.IsEnabled ? _notifyCharacteristic : null;
        if (ReferenceEquals(target, _notificationCaptureCharacteristic))
            return;

        DetachMainNotificationCaptureHandler();
        if (target == null)
            return;

        _notificationMonitorKissDecoder = new KissStreamDecoder();
        _terminalNotificationMode = target.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? "Notify"
            : target.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate)
                ? "Indicate"
                : "Notify/Indicate";

        target.ValueChanged += NotificationCaptureCharacteristic_ValueChanged;
        _notificationCaptureCharacteristic = target;
    }

    private void DetachMainNotificationCaptureHandler()
    {
        if (_notificationCaptureCharacteristic != null)
        {
            try { _notificationCaptureCharacteristic.ValueChanged -= NotificationCaptureCharacteristic_ValueChanged; } catch { }
        }
        _notificationCaptureCharacteristic = null;
        _notificationMonitorKissDecoder = new KissStreamDecoder();
    }

    private void NotificationCaptureCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using DataReader reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] data = new byte[checked((int)reader.UnconsumedBufferLength)];
            reader.ReadBytes(data);

            // Raw BLE capture is always first and never depends on RT950/KISS state.
            NotificationRecord record = CaptureMainNotification(sender, data);

            int? kissFrames = null;
            if (IsKissRxProcessingEnabled)
            {
                List<KissFrame> completed = _notificationMonitorKissDecoder.Push(data).ToList();
                kissFrames = completed.Count;
                if (_optionalFeatureSettings.KissShowRawFrames)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        foreach (KissFrame frame in completed)
                            LogKissFeatureFrame(sender, frame);
                    });
                }
            }

            record.SetKissFrameCount(kissFrames);
        }
        catch
        {
            // Monitoring/protocol helpers are diagnostic-only and must never interfere with BLE RX.
        }
    }

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
