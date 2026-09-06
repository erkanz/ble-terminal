using System.Collections.Concurrent;
using System.Windows.Threading;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BLESerialTerminal;

public partial class GattInspectorWindow
{
    private readonly HashSet<GattCharacteristic> _notificationCaptureHandlers = new();
    private readonly ConcurrentDictionary<GattCharacteristic, NotificationSubscriptionMetadata> _notificationCaptureMetadata = new();
    private readonly ConcurrentDictionary<GattCharacteristic, KissStreamDecoder> _notificationCaptureKissDecoders = new();
    private DispatcherTimer? _notificationCaptureSyncTimer;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _notificationCaptureSyncTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _notificationCaptureSyncTimer.Tick += NotificationCaptureSyncTimer_Tick;
        _notificationCaptureSyncTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_notificationCaptureSyncTimer != null)
        {
            _notificationCaptureSyncTimer.Stop();
            _notificationCaptureSyncTimer.Tick -= NotificationCaptureSyncTimer_Tick;
            _notificationCaptureSyncTimer = null;
        }

        foreach (GattCharacteristic characteristic in _notificationCaptureHandlers.ToArray())
        {
            try { characteristic.ValueChanged -= NotificationCaptureCharacteristic_ValueChanged; } catch { }
        }
        _notificationCaptureHandlers.Clear();
        _notificationCaptureMetadata.Clear();
        _notificationCaptureKissDecoders.Clear();
        base.OnClosed(e);
    }

    private void NotificationCaptureSyncTimer_Tick(object? sender, EventArgs e) =>
        SyncInspectorNotificationCaptureHandlers();

    private void SyncInspectorNotificationCaptureHandlers()
    {
        var active = _subscribedCharacteristics.ToHashSet();

        foreach (GattCharacteristic characteristic in active)
        {
            GattCharacteristicInfo? info = _services
                .SelectMany(service => service.Characteristics)
                .FirstOrDefault(candidate => ReferenceEquals(candidate.Characteristic, characteristic));

            if (info != null)
            {
                string mode = info.NotifyEnabled
                    ? "Notify"
                    : info.IndicateEnabled
                        ? "Indicate"
                        : "Notify/Indicate";

                _notificationCaptureMetadata[characteristic] = new NotificationSubscriptionMetadata(
                    info.Service.Uuid,
                    info.Source,
                    info.Reused,
                    mode);
            }

            _notificationCaptureKissDecoders.TryAdd(characteristic, new KissStreamDecoder());

            if (_notificationCaptureHandlers.Add(characteristic))
                characteristic.ValueChanged += NotificationCaptureCharacteristic_ValueChanged;
        }

        foreach (GattCharacteristic characteristic in _notificationCaptureHandlers.Where(c => !active.Contains(c)).ToArray())
        {
            try { characteristic.ValueChanged -= NotificationCaptureCharacteristic_ValueChanged; } catch { }
            _notificationCaptureHandlers.Remove(characteristic);
            _notificationCaptureMetadata.TryRemove(characteristic, out _);
            _notificationCaptureKissDecoders.TryRemove(characteristic, out _);
        }
    }

    private void NotificationCaptureCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            // The main terminal has its own capture handler for the Auto Detect data characteristic.
            // Do not double-count the same FFE1 notification when Inspector merely reuses that CCCD.
            if (_autoGatt?.NotifyCharacteristic != null && ReferenceEquals(sender, _autoGatt.NotifyCharacteristic))
                return;

            byte[] data = BufferToBytes(args.CharacteristicValue);
            _notificationCaptureMetadata.TryGetValue(sender, out NotificationSubscriptionMetadata? metadata);

            NotificationRecord record = NotificationCaptureHub.Store.Add(
                _deviceName,
                metadata?.ServiceUuid,
                sender.Uuid,
                metadata?.Source ?? "GATT INSPECTOR",
                metadata?.Reused ?? false,
                metadata?.DeliveryMode ?? "Notify/Indicate",
                data);

            if (_notificationCaptureKissDecoders.TryGetValue(sender, out KissStreamDecoder? decoder))
                record.SetKissFrameCount(decoder.Push(data).Count());
        }
        catch
        {
            // Capture is diagnostic-only and must never alter Inspector notification behavior.
        }
    }
}
