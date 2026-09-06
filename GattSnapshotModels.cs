namespace BLESerialTerminal;

internal static class GattSnapshotFormat
{
    public const string Name = "BLESerialTerminalGattSnapshot";
    public const int CurrentVersion = 1;
    public const int MinimumSupportedVersion = 1;
}

internal sealed class GattSnapshot
{
    public string Format { get; set; } = GattSnapshotFormat.Name;
    public int Version { get; set; } = GattSnapshotFormat.CurrentVersion;
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public GattSnapshotMetadata Metadata { get; set; } = new();
    public List<GattSnapshotService> Services { get; set; } = new();
    public List<string> DiscoveryEvidence { get; set; } = new();
}

internal sealed class GattSnapshotMetadata
{
    public string ApplicationVersion { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public DateTime? ConnectionTimestamp { get; set; }
    public string ConnectionState { get; set; } = string.Empty;
    public string GattState { get; set; } = string.Empty;
    public string ServiceDiscoveryStatus { get; set; } = string.Empty;

    public string AutoProfile { get; set; } = string.Empty;
    public string AutoServiceUuid { get; set; } = string.Empty;
    public string AutoWriteUuid { get; set; } = string.Empty;
    public string AutoNotifyUuid { get; set; } = string.Empty;
    public bool AutoSameCharacteristic { get; set; }

    public string ManualServiceUuid { get; set; } = string.Empty;
    public string ManualWriteUuid { get; set; } = string.Empty;
    public string ManualNotifyUuid { get; set; } = string.Empty;
    public string ManualWriteType { get; set; } = string.Empty;
    public bool ManualOverrideActive { get; set; }
}

internal sealed class GattSnapshotService
{
    public string ShortUuid { get; set; } = string.Empty;
    public string FullUuid { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool Reused { get; set; }
    public string Ownership { get; set; } = string.Empty;
    public string DiscoveryStatus { get; set; } = string.Empty;
    public List<GattSnapshotCharacteristic> Characteristics { get; set; } = new();
}

internal sealed class GattSnapshotCharacteristic
{
    public string ShortUuid { get; set; } = string.Empty;
    public string FullUuid { get; set; } = string.Empty;
    public string Properties { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool Reused { get; set; }
    public string Ownership { get; set; } = string.Empty;
    public bool ReadableAdvertised { get; set; }
    public bool WritableWithResponseAdvertised { get; set; }
    public bool WritableWithoutResponseAdvertised { get; set; }
    public bool NotifyAdvertised { get; set; }
    public bool IndicateAdvertised { get; set; }
    public bool CccdKnownActive { get; set; }
    public string DescriptorDiscoveryStatus { get; set; } = string.Empty;
    public List<GattSnapshotDescriptor> Descriptors { get; set; } = new();
}

internal sealed class GattSnapshotDescriptor
{
    public string ShortUuid { get; set; } = string.Empty;
    public string FullUuid { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

internal sealed class GattSnapshotRouteState
{
    public string AutoProfile { get; set; } = string.Empty;
    public string AutoServiceUuid { get; set; } = string.Empty;
    public string AutoWriteUuid { get; set; } = string.Empty;
    public string AutoNotifyUuid { get; set; } = string.Empty;
    public bool AutoSameCharacteristic { get; set; }

    public string ManualServiceUuid { get; set; } = string.Empty;
    public string ManualWriteUuid { get; set; } = string.Empty;
    public string ManualNotifyUuid { get; set; } = string.Empty;
    public string ManualWriteType { get; set; } = string.Empty;
    public bool ManualOverrideActive { get; set; }
}

internal sealed class GattSnapshotDiffRow
{
    public string Change { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string Before { get; set; } = string.Empty;
    public string After { get; set; } = string.Empty;

    public string Detail => $"{Change} {Scope} {Path} {Field}: '{Before}' -> '{After}'";
}
