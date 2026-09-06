using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BLESerialTerminal;

internal sealed class GattCharacteristicChoice
{
    public GattCharacteristicChoice(GattCharacteristic characteristic)
    {
        Characteristic = characteristic ?? throw new ArgumentNullException(nameof(characteristic));
    }

    public GattCharacteristic Characteristic { get; }
    public string ShortUuid => BleUuid.Short(Characteristic.Uuid);
    public string FullUuid => BleUuid.Full(Characteristic.Uuid);
    public GattCharacteristicProperties Properties => Characteristic.CharacteristicProperties;
    public bool CanWriteWithResponse => Properties.HasFlag(GattCharacteristicProperties.Write);
    public bool CanWriteWithoutResponse => Properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
    public bool CanWrite => CanWriteWithResponse || CanWriteWithoutResponse;
    public bool CanNotify => Properties.HasFlag(GattCharacteristicProperties.Notify) || Properties.HasFlag(GattCharacteristicProperties.Indicate);

    public string Display
    {
        get
        {
            var flags = new List<string>();
            if (CanWriteWithResponse) flags.Add("Write");
            if (CanWriteWithoutResponse) flags.Add("WriteNoRsp");
            if (Properties.HasFlag(GattCharacteristicProperties.Notify)) flags.Add("Notify");
            if (Properties.HasFlag(GattCharacteristicProperties.Indicate)) flags.Add("Indicate");
            if (Properties.HasFlag(GattCharacteristicProperties.Read)) flags.Add("Read");
            return flags.Count == 0 ? ShortUuid : $"{ShortUuid}  [{string.Join(", ", flags)}]";
        }
    }
}

internal sealed class TxCommandPreferencesData
{
    public int RowCount { get; set; } = 1;
    public List<TxCommandSlotData> Slots { get; set; } = new();
}

internal sealed class TxCommandSlotData
{
    public int Slot { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
}

internal sealed record TxCommandRequest(
    int Slot,
    string Label,
    string ServiceUuid,
    string WriteUuid,
    bool WithResponse,
    byte[] Payload,
    ulong? BluetoothAddress,
    DateTime? ConnectedAt,
    int ChunkSize);
