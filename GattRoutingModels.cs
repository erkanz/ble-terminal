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

internal static class CommandOverrideValues
{
    public const string UseGlobal = "Use Global";
    public const string WithResponse = "With Response";
    public const string WithoutResponse = "Without Response";
    public const string Hex = "HEX";
    public const string Text = "TEXT";
    public const string None = "NONE";
    public const string Lf = "LF";
    public const string Cr = "CR";
    public const string CrLf = "CRLF";
}

internal sealed class CommandWorkspaceSettings
{
    public int Version { get; set; } = 2;
    public string ActiveGroup { get; set; } = "General";
    public bool ConfirmRemove { get; set; } = true;
    public List<CommandRowData> Rows { get; set; } = new();
}

internal sealed class CommandRowData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Group { get; set; } = "General";
    public string Label { get; set; } = "Command";
    public string Command { get; set; } = string.Empty;
    public string TargetOverride { get; set; } = CommandOverrideValues.UseGlobal;
    public string WriteTypeOverride { get; set; } = CommandOverrideValues.UseGlobal;
    public string TxModeOverride { get; set; } = CommandOverrideValues.UseGlobal;
    public string LineEndingOverride { get; set; } = CommandOverrideValues.UseGlobal;
    public string ModuleTag { get; set; } = string.Empty;
    public string PresetKey { get; set; } = string.Empty;
    public bool RequiresRt950Unlock { get; set; }

    public CommandRowData Clone() => new()
    {
        Id = Id,
        Group = Group,
        Label = Label,
        Command = Command,
        TargetOverride = TargetOverride,
        WriteTypeOverride = WriteTypeOverride,
        TxModeOverride = TxModeOverride,
        LineEndingOverride = LineEndingOverride,
        ModuleTag = ModuleTag,
        PresetKey = PresetKey,
        RequiresRt950Unlock = RequiresRt950Unlock
    };
}

// Legacy v1 settings are retained only for one-time migration. The active workspace has no fixed row limit.
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
    string CommandId,
    string Label,
    string ServiceUuid,
    string WriteUuid,
    bool WithResponse,
    bool IsHex,
    TxLineEnding LineEnding,
    byte[] Payload,
    ulong? BluetoothAddress,
    DateTime? ConnectedAt,
    int ChunkSize,
    string ModuleTag,
    bool RequiresRt950Unlock);
