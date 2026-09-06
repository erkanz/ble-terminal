using System.Text;
using System.Text.Json;

namespace BLESerialTerminal;

internal static class GattSnapshotSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string ToJson(GattSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Normalize(snapshot);
        return JsonSerializer.Serialize(snapshot, Options);
    }

    public static GattSnapshot FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("GATT snapshot file is empty.");

        GattSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<GattSnapshot>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid GATT snapshot JSON: {ex.Message}", ex);
        }

        if (snapshot == null)
            throw new InvalidDataException("GATT snapshot could not be decoded.");
        if (!string.Equals(snapshot.Format, GattSnapshotFormat.Name, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported snapshot format '{snapshot.Format}'.");
        if (snapshot.Version < GattSnapshotFormat.MinimumSupportedVersion)
            throw new InvalidDataException($"Snapshot version {snapshot.Version} is older than this application supports.");
        if (snapshot.Version > GattSnapshotFormat.CurrentVersion)
            throw new InvalidDataException($"Snapshot version {snapshot.Version} is newer than this application supports.");

        Normalize(snapshot);
        return snapshot;
    }

    public static string ToText(GattSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Normalize(snapshot);

        var sb = new StringBuilder();
        GattSnapshotMetadata m = snapshot.Metadata;
        sb.AppendLine("BLE SERIAL TERMINAL - GATT SNAPSHOT");
        sb.AppendLine("===================================");
        sb.AppendLine($"Format: {snapshot.Format} v{snapshot.Version}");
        sb.AppendLine($"Captured UTC: {snapshot.CapturedAtUtc:O}");
        sb.AppendLine($"Application: {m.ApplicationVersion}");
        sb.AppendLine($"Device: {m.DeviceName}");
        sb.AppendLine($"Address: {m.Address}");
        sb.AppendLine($"Connection timestamp: {(m.ConnectionTimestamp.HasValue ? m.ConnectionTimestamp.Value.ToString("O") : "-")}");
        sb.AppendLine($"Connection state: {m.ConnectionState}");
        sb.AppendLine($"GATT state: {m.GattState}");
        sb.AppendLine($"Service discovery: {m.ServiceDiscoveryStatus}");
        sb.AppendLine();
        sb.AppendLine("AUTO DETECT BASELINE");
        sb.AppendLine($"Profile: {m.AutoProfile}");
        sb.AppendLine($"Service: {m.AutoServiceUuid}");
        sb.AppendLine($"Write: {m.AutoWriteUuid}");
        sb.AppendLine($"Notify: {m.AutoNotifyUuid}");
        sb.AppendLine($"Same characteristic: {m.AutoSameCharacteristic}");
        sb.AppendLine();
        sb.AppendLine("CURRENT MANUAL ROUTE");
        sb.AppendLine($"Service: {m.ManualServiceUuid}");
        sb.AppendLine($"Write: {m.ManualWriteUuid}");
        sb.AppendLine($"Notify: {m.ManualNotifyUuid}");
        sb.AppendLine($"Write type: {m.ManualWriteType}");
        sb.AppendLine($"Override active: {m.ManualOverrideActive}");
        sb.AppendLine();

        foreach (GattSnapshotService service in snapshot.Services)
        {
            sb.AppendLine($"SERVICE {service.ShortUuid} [{service.FullUuid}]");
            sb.AppendLine($"  SOURCE={service.Source} REUSED={service.Reused} OWNERSHIP={service.Ownership}");
            sb.AppendLine($"  DISCOVERY_STATUS={service.DiscoveryStatus}");
            foreach (GattSnapshotCharacteristic characteristic in service.Characteristics)
            {
                sb.AppendLine($"  CHARACTERISTIC {characteristic.ShortUuid} [{characteristic.FullUuid}]");
                sb.AppendLine($"    PROPERTIES={characteristic.Properties}");
                sb.AppendLine($"    SOURCE={characteristic.Source} REUSED={characteristic.Reused} OWNERSHIP={characteristic.Ownership}");
                sb.AppendLine($"    READ={YesNo(characteristic.ReadableAdvertised)} WRITE={YesNo(characteristic.WritableWithResponseAdvertised)} WRITE_NO_RESPONSE={YesNo(characteristic.WritableWithoutResponseAdvertised)} NOTIFY={YesNo(characteristic.NotifyAdvertised)} INDICATE={YesNo(characteristic.IndicateAdvertised)}");
                sb.AppendLine($"    CCCD_KNOWN_ACTIVE={YesNo(characteristic.CccdKnownActive)}");
                sb.AppendLine($"    DESCRIPTOR_DISCOVERY={characteristic.DescriptorDiscoveryStatus}");
                foreach (GattSnapshotDescriptor descriptor in characteristic.Descriptors)
                    sb.AppendLine($"    DESCRIPTOR {descriptor.ShortUuid} [{descriptor.FullUuid}] SOURCE={descriptor.Source}");
            }
            sb.AppendLine();
        }

        if (snapshot.DiscoveryEvidence.Count > 0)
        {
            sb.AppendLine("DISCOVERY / ACCESS EVIDENCE");
            sb.AppendLine("---------------------------");
            foreach (string line in snapshot.DiscoveryEvidence)
                sb.AppendLine(line);
        }

        return sb.ToString();
    }

    public static void Normalize(GattSnapshot snapshot)
    {
        snapshot.Metadata ??= new GattSnapshotMetadata();
        snapshot.Services ??= new List<GattSnapshotService>();
        snapshot.DiscoveryEvidence ??= new List<string>();

        foreach (GattSnapshotService service in snapshot.Services)
        {
            service.Characteristics ??= new List<GattSnapshotCharacteristic>();
            foreach (GattSnapshotCharacteristic characteristic in service.Characteristics)
            {
                characteristic.Descriptors ??= new List<GattSnapshotDescriptor>();
                characteristic.Descriptors = characteristic.Descriptors
                    .OrderBy(d => d.FullUuid, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.Source, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            service.Characteristics = service.Characteristics
                .OrderBy(c => c.FullUuid, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Source, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        snapshot.Services = snapshot.Services
            .OrderBy(s => s.FullUuid, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string YesNo(bool value) => value ? "YES" : "NO";
}
