using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BLESerialTerminal;

internal sealed class GattServiceInfo
{
    public required GattDeviceService Service { get; init; }
    public List<GattCharacteristicInfo> Characteristics { get; } = new();
    public string Source { get; init; } = "DISCOVERED";
    public bool Reused { get; init; }
    public bool OwnsService { get; init; } = true;
    public string DiscoveryStatus { get; set; } = "Success";
    public string Display => Reused
        ? $"Service: {BleUuid.Display(Service.Uuid)}   [Source: {Source}; Reused: Yes]"
        : $"Service: {BleUuid.Display(Service.Uuid)}";
}

internal sealed class GattCharacteristicInfo
{
    public required GattDeviceService Service { get; init; }
    public required GattCharacteristic Characteristic { get; init; }
    public List<GattDescriptor> Descriptors { get; } = new();
    public string Source { get; init; } = "DISCOVERED";
    public bool Reused { get; init; }
    public bool NotifyEnabled { get; set; }
    public bool IndicateEnabled { get; set; }
    public bool CccdKnownActive { get; set; }
    public string ShortUuid => BleUuid.Short(Characteristic.Uuid);
    public string Display => Reused
        ? $"Characteristic: {BleUuid.Display(Characteristic.Uuid)}   [Source: {Source}; Reused: Yes]"
        : $"Characteristic: {BleUuid.Display(Characteristic.Uuid)}";
}
