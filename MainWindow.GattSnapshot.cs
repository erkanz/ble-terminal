namespace BLESerialTerminal;

public partial class MainWindow
{
    internal GattSnapshotRouteState GetGattSnapshotRouteState()
    {
        string autoService = _autoGatt.Service == null ? string.Empty : BleUuid.Full(_autoGatt.Service.Uuid);
        string autoWrite = _autoGatt.WriteCharacteristic == null ? string.Empty : BleUuid.Full(_autoGatt.WriteCharacteristic.Uuid);
        string autoNotify = _autoGatt.NotifyCharacteristic == null ? string.Empty : BleUuid.Full(_autoGatt.NotifyCharacteristic.Uuid);
        string manualService = _service == null ? string.Empty : BleUuid.Full(_service.Uuid);
        string manualWrite = _writeCharacteristic == null ? string.Empty : BleUuid.Full(_writeCharacteristic.Uuid);
        string manualNotify = _notifyCharacteristic == null ? string.Empty : BleUuid.Full(_notifyCharacteristic.Uuid);
        string writeType = WriteTypeComboBox.SelectedIndex == 1 ? "WITHOUT_RESPONSE" : "WITH_RESPONSE";

        bool overrideActive = _autoGatt.IsAvailable &&
            (!string.Equals(autoService, manualService, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(autoWrite, manualWrite, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(autoNotify, manualNotify, StringComparison.OrdinalIgnoreCase));

        return new GattSnapshotRouteState
        {
            AutoProfile = _autoGatt.ProfileName,
            AutoServiceUuid = autoService,
            AutoWriteUuid = autoWrite,
            AutoNotifyUuid = autoNotify,
            AutoSameCharacteristic = _autoGatt.SameCharacteristic,
            ManualServiceUuid = manualService,
            ManualWriteUuid = manualWrite,
            ManualNotifyUuid = manualNotify,
            ManualWriteType = writeType,
            ManualOverrideActive = overrideActive
        };
    }
}
