namespace BLESerialTerminal;

internal static class GattSnapshotComparer
{
    public static List<GattSnapshotDiffRow> Compare(GattSnapshot before, GattSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        GattSnapshotSerializer.Normalize(before);
        GattSnapshotSerializer.Normalize(after);

        var rows = new List<GattSnapshotDiffRow>();
        CompareMetadata(before.Metadata, after.Metadata, rows);

        Dictionary<string, GattSnapshotService> aServices = IndexByUuid(before.Services, s => s.FullUuid);
        Dictionary<string, GattSnapshotService> bServices = IndexByUuid(after.Services, s => s.FullUuid);

        foreach (string key in aServices.Keys.Union(bServices.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            bool inA = aServices.TryGetValue(key, out GattSnapshotService? a);
            bool inB = bServices.TryGetValue(key, out GattSnapshotService? b);
            if (!inA)
            {
                Add(rows, "ADDED", "SERVICE", ServicePath(b!), "Presence", "-", "Present");
                continue;
            }
            if (!inB)
            {
                Add(rows, "REMOVED", "SERVICE", ServicePath(a!), "Presence", "Present", "-");
                continue;
            }

            string servicePath = ServicePath(a!);
            CompareField(rows, "SERVICE", servicePath, "DiscoveryStatus", a!.DiscoveryStatus, b!.DiscoveryStatus);
            CompareField(rows, "SERVICE", servicePath, "Source", a.Source, b.Source);
            CompareField(rows, "SERVICE", servicePath, "Reused", a.Reused, b.Reused);
            CompareField(rows, "SERVICE", servicePath, "Ownership", a.Ownership, b.Ownership);
            CompareCharacteristics(a, b, rows);
        }

        return rows
            .OrderBy(r => ScopeOrder(r.Scope))
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Field, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Change, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CompareMetadata(GattSnapshotMetadata a, GattSnapshotMetadata b, List<GattSnapshotDiffRow> rows)
    {
        CompareField(rows, "MAPPING", "Auto Detect", "Profile", a.AutoProfile, b.AutoProfile);
        CompareField(rows, "MAPPING", "Auto Detect", "Service", a.AutoServiceUuid, b.AutoServiceUuid);
        CompareField(rows, "MAPPING", "Auto Detect", "Write", a.AutoWriteUuid, b.AutoWriteUuid);
        CompareField(rows, "MAPPING", "Auto Detect", "Notify", a.AutoNotifyUuid, b.AutoNotifyUuid);
        CompareField(rows, "MAPPING", "Auto Detect", "SameCharacteristic", a.AutoSameCharacteristic, b.AutoSameCharacteristic);
        CompareField(rows, "MAPPING", "Manual Route", "Service", a.ManualServiceUuid, b.ManualServiceUuid);
        CompareField(rows, "MAPPING", "Manual Route", "Write", a.ManualWriteUuid, b.ManualWriteUuid);
        CompareField(rows, "MAPPING", "Manual Route", "Notify", a.ManualNotifyUuid, b.ManualNotifyUuid);
        CompareField(rows, "MAPPING", "Manual Route", "WriteType", a.ManualWriteType, b.ManualWriteType);
        CompareField(rows, "MAPPING", "Manual Route", "OverrideActive", a.ManualOverrideActive, b.ManualOverrideActive);
        CompareField(rows, "ACCESS", "Discovery", "GATT state", a.GattState, b.GattState);
        CompareField(rows, "ACCESS", "Discovery", "Service discovery", a.ServiceDiscoveryStatus, b.ServiceDiscoveryStatus);
    }

    private static void CompareCharacteristics(GattSnapshotService aService, GattSnapshotService bService, List<GattSnapshotDiffRow> rows)
    {
        Dictionary<string, GattSnapshotCharacteristic> aChars = IndexByUuid(aService.Characteristics, c => c.FullUuid);
        Dictionary<string, GattSnapshotCharacteristic> bChars = IndexByUuid(bService.Characteristics, c => c.FullUuid);

        foreach (string key in aChars.Keys.Union(bChars.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            bool inA = aChars.TryGetValue(key, out GattSnapshotCharacteristic? a);
            bool inB = bChars.TryGetValue(key, out GattSnapshotCharacteristic? b);
            if (!inA)
            {
                Add(rows, "ADDED", "CHARACTERISTIC", CharacteristicPath(bService, b!), "Presence", "-", "Present");
                continue;
            }
            if (!inB)
            {
                Add(rows, "REMOVED", "CHARACTERISTIC", CharacteristicPath(aService, a!), "Presence", "Present", "-");
                continue;
            }

            string path = CharacteristicPath(aService, a!);
            CompareField(rows, "CHARACTERISTIC", path, "Properties", a!.Properties, b!.Properties);
            CompareField(rows, "CHARACTERISTIC", path, "Source", a.Source, b.Source);
            CompareField(rows, "CHARACTERISTIC", path, "Reused", a.Reused, b.Reused);
            CompareField(rows, "CHARACTERISTIC", path, "Ownership", a.Ownership, b.Ownership);
            CompareField(rows, "CHARACTERISTIC", path, "Read", a.ReadableAdvertised, b.ReadableAdvertised);
            CompareField(rows, "CHARACTERISTIC", path, "Write", a.WritableWithResponseAdvertised, b.WritableWithResponseAdvertised);
            CompareField(rows, "CHARACTERISTIC", path, "WriteWithoutResponse", a.WritableWithoutResponseAdvertised, b.WritableWithoutResponseAdvertised);
            CompareField(rows, "CHARACTERISTIC", path, "Notify", a.NotifyAdvertised, b.NotifyAdvertised);
            CompareField(rows, "CHARACTERISTIC", path, "Indicate", a.IndicateAdvertised, b.IndicateAdvertised);
            CompareField(rows, "CHARACTERISTIC", path, "CccdKnownActive", a.CccdKnownActive, b.CccdKnownActive);
            CompareField(rows, "ACCESS", path, "DescriptorDiscoveryStatus", a.DescriptorDiscoveryStatus, b.DescriptorDiscoveryStatus);
            CompareDescriptors(aService, a, b, rows);
        }
    }

    private static void CompareDescriptors(
        GattSnapshotService service,
        GattSnapshotCharacteristic aCharacteristic,
        GattSnapshotCharacteristic bCharacteristic,
        List<GattSnapshotDiffRow> rows)
    {
        Dictionary<string, GattSnapshotDescriptor> aDescriptors = IndexByUuid(aCharacteristic.Descriptors, d => d.FullUuid);
        Dictionary<string, GattSnapshotDescriptor> bDescriptors = IndexByUuid(bCharacteristic.Descriptors, d => d.FullUuid);

        foreach (string key in aDescriptors.Keys.Union(bDescriptors.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            bool inA = aDescriptors.TryGetValue(key, out GattSnapshotDescriptor? a);
            bool inB = bDescriptors.TryGetValue(key, out GattSnapshotDescriptor? b);
            if (!inA)
            {
                Add(rows, "ADDED", "DESCRIPTOR", DescriptorPath(service, bCharacteristic, b!), "Presence", "-", "Present");
                continue;
            }
            if (!inB)
            {
                Add(rows, "REMOVED", "DESCRIPTOR", DescriptorPath(service, aCharacteristic, a!), "Presence", "Present", "-");
                continue;
            }

            CompareField(rows, "DESCRIPTOR", DescriptorPath(service, aCharacteristic, a!), "Source", a!.Source, b!.Source);
        }
    }

    private static Dictionary<string, T> IndexByUuid<T>(IEnumerable<T> source, Func<T, string> uuidSelector)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, T> group in source.GroupBy(uuidSelector, StringComparer.OrdinalIgnoreCase))
        {
            int index = 0;
            foreach (T item in group)
            {
                index++;
                string key = group.Count() == 1 ? group.Key : $"{group.Key}#{index}";
                result[key] = item;
            }
        }
        return result;
    }

    private static string ServicePath(GattSnapshotService service) =>
        string.IsNullOrWhiteSpace(service.ShortUuid) ? service.FullUuid : service.ShortUuid;

    private static string CharacteristicPath(GattSnapshotService service, GattSnapshotCharacteristic characteristic) =>
        $"{ServicePath(service)}/{(string.IsNullOrWhiteSpace(characteristic.ShortUuid) ? characteristic.FullUuid : characteristic.ShortUuid)}";

    private static string DescriptorPath(GattSnapshotService service, GattSnapshotCharacteristic characteristic, GattSnapshotDescriptor descriptor) =>
        $"{CharacteristicPath(service, characteristic)}/{(string.IsNullOrWhiteSpace(descriptor.ShortUuid) ? descriptor.FullUuid : descriptor.ShortUuid)}";

    private static void CompareField(List<GattSnapshotDiffRow> rows, string scope, string path, string field, object? before, object? after)
    {
        string a = before?.ToString() ?? string.Empty;
        string b = after?.ToString() ?? string.Empty;
        if (!string.Equals(a, b, StringComparison.Ordinal))
            Add(rows, "CHANGED", scope, path, field, a, b);
    }

    private static void Add(List<GattSnapshotDiffRow> rows, string change, string scope, string path, string field, string before, string after)
    {
        rows.Add(new GattSnapshotDiffRow
        {
            Change = change,
            Scope = scope,
            Path = path,
            Field = field,
            Before = before,
            After = after
        });
    }

    private static int ScopeOrder(string scope) => scope switch
    {
        "MAPPING" => 0,
        "ACCESS" => 1,
        "SERVICE" => 2,
        "CHARACTERISTIC" => 3,
        "DESCRIPTOR" => 4,
        _ => 9
    };
}
