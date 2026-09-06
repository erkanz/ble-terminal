using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BLESerialTerminal;

internal sealed class AutoDetectedGattContext
{
    public GattDeviceService? Service { get; set; }
    public GattCharacteristic? WriteCharacteristic { get; set; }
    public GattCharacteristic? NotifyCharacteristic { get; set; }
    public List<GattCharacteristic> ServiceCharacteristics { get; } = new();
    public string ProfileName { get; set; } = string.Empty;
    public bool IsRadtelRt950Kiss { get; set; }
    public bool NotifyHandlerAttached { get; set; }
    public bool CccdEnabled { get; set; }

    public bool IsAvailable => Service != null && WriteCharacteristic != null && NotifyCharacteristic != null;
    public bool SameCharacteristic => WriteCharacteristic != null && ReferenceEquals(WriteCharacteristic, NotifyCharacteristic);

    public void Reset()
    {
        Service = null;
        WriteCharacteristic = null;
        NotifyCharacteristic = null;
        ServiceCharacteristics.Clear();
        ProfileName = string.Empty;
        IsRadtelRt950Kiss = false;
        NotifyHandlerAttached = false;
        CccdEnabled = false;
    }
}
