using BLESerialTerminal;

int passed = 0;

void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception($"FAIL  {name}");
    passed++;
    Console.WriteLine($"PASS  {name}");
}

GattSnapshot BuildBaseline()
{
    return new GattSnapshot
    {
        CapturedAtUtc = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc),
        Metadata = new GattSnapshotMetadata
        {
            ApplicationVersion = "1.0.0",
            DeviceName = "walkie-talkie",
            Address = "20:6E:F1:A7:51:4D",
            ConnectionState = "Connected",
            GattState = "GATT: DISCOVERED",
            ServiceDiscoveryStatus = "GetGattServicesAsync status=Success statusCode=0 protocol=none",
            AutoProfile = "RADTEL_RT950_KISS",
            AutoServiceUuid = "0000FFE0-0000-1000-8000-00805F9B34FB",
            AutoWriteUuid = "0000FFE1-0000-1000-8000-00805F9B34FB",
            AutoNotifyUuid = "0000FFE1-0000-1000-8000-00805F9B34FB",
            AutoSameCharacteristic = true,
            ManualServiceUuid = "0000FFE0-0000-1000-8000-00805F9B34FB",
            ManualWriteUuid = "0000FFE1-0000-1000-8000-00805F9B34FB",
            ManualNotifyUuid = "0000FFE1-0000-1000-8000-00805F9B34FB",
            ManualWriteType = "WITH_RESPONSE",
            ManualOverrideActive = false
        },
        Services = new List<GattSnapshotService>
        {
            new()
            {
                ShortUuid = "FFE0",
                FullUuid = "0000FFE0-0000-1000-8000-00805F9B34FB",
                Source = "AUTO-DETECT/REUSED",
                Reused = true,
                Ownership = "AUTO-DETECT/OWNER",
                DiscoveryStatus = "Reused",
                Characteristics = new List<GattSnapshotCharacteristic>
                {
                    new()
                    {
                        ShortUuid = "FFE1",
                        FullUuid = "0000FFE1-0000-1000-8000-00805F9B34FB",
                        Properties = "Write, Notify",
                        Source = "AUTO-DETECT/REUSED",
                        Reused = true,
                        Ownership = "AUTO-DETECT/OWNER",
                        WritableWithResponseAdvertised = true,
                        NotifyAdvertised = true,
                        CccdKnownActive = true,
                        DescriptorDiscoveryStatus = "CAPTURED count=1",
                        Descriptors = new List<GattSnapshotDescriptor>
                        {
                            new()
                            {
                                ShortUuid = "2902",
                                FullUuid = "00002902-0000-1000-8000-00805F9B34FB",
                                Source = "AUTO-DETECT/REUSED"
                            }
                        }
                    },
                    new()
                    {
                        ShortUuid = "FF31",
                        FullUuid = "0000FF31-0000-1000-8000-00805F9B34FB",
                        Properties = "Write",
                        Source = "AUTO-DETECT/REUSED",
                        Reused = true,
                        Ownership = "AUTO-DETECT/OWNER",
                        WritableWithResponseAdvertised = true,
                        DescriptorDiscoveryStatus = "NONE_OR_NOT_ENUMERATED; see discovery evidence"
                    }
                }
            }
        },
        DiscoveryEvidence = new List<string>
        {
            "GetGattServicesAsync status=Success statusCode=0 protocol=none",
            "*** USING EXISTING AUTO-DETECT GATT OBJECTS"
        }
    };
}

GattSnapshot Clone(GattSnapshot source) => GattSnapshotSerializer.FromJson(GattSnapshotSerializer.ToJson(source));

GattSnapshot baseline = BuildBaseline();
string json = GattSnapshotSerializer.ToJson(baseline);
GattSnapshot roundTrip = GattSnapshotSerializer.FromJson(json);
Check(roundTrip.Services.Count == 1 && roundTrip.Services[0].Characteristics.Count == 2, "snapshot JSON round trip preserves GATT tree");
Check(roundTrip.Services[0].Characteristics.Any(c => c.ShortUuid == "FFE1" && c.Descriptors.Any(d => d.ShortUuid == "2902")), "snapshot JSON round trip preserves descriptors");
Check(roundTrip.Metadata.AutoProfile == "RADTEL_RT950_KISS" && roundTrip.Metadata.ManualWriteUuid == "0000FFE1-0000-1000-8000-00805F9B34FB", "snapshot metadata survives round trip");

string withUnknown = json.TrimEnd().TrimEnd('}') + ",\n  \"futureField\": { \"ignored\": true }\n}";
GattSnapshot unknownLoaded = GattSnapshotSerializer.FromJson(withUnknown);
Check(unknownLoaded.Services.Count == 1, "unknown future JSON fields are ignored");

string future = json.Replace("\"version\": 1", "\"version\": 99", StringComparison.Ordinal);
bool futureRejected = false;
try { _ = GattSnapshotSerializer.FromJson(future); }
catch (InvalidDataException) { futureRejected = true; }
Check(futureRejected, "unsupported future snapshot version rejected safely");

List<GattSnapshotDiffRow> identical = GattSnapshotComparer.Compare(Clone(baseline), Clone(baseline));
Check(identical.Count == 0, "identical snapshots produce no diff");

GattSnapshot addedCharacteristic = Clone(baseline);
addedCharacteristic.Services[0].Characteristics.Add(new GattSnapshotCharacteristic
{
    ShortUuid = "FF32",
    FullUuid = "0000FF32-0000-1000-8000-00805F9B34FB",
    Properties = "Notify",
    Source = "DISCOVERED",
    NotifyAdvertised = true,
    Ownership = "INSPECTOR/DISCOVERED"
});
List<GattSnapshotDiffRow> addedRows = GattSnapshotComparer.Compare(Clone(baseline), addedCharacteristic);
Check(addedRows.Any(r => r.Change == "ADDED" && r.Scope == "CHARACTERISTIC" && r.Path.Contains("FF32")), "added characteristic detected");

GattSnapshot removedDescriptor = Clone(baseline);
removedDescriptor.Services[0].Characteristics.First(c => c.ShortUuid == "FFE1").Descriptors.Clear();
List<GattSnapshotDiffRow> descriptorRows = GattSnapshotComparer.Compare(Clone(baseline), removedDescriptor);
Check(descriptorRows.Any(r => r.Change == "REMOVED" && r.Scope == "DESCRIPTOR" && r.Path.Contains("2902")), "removed descriptor detected");

GattSnapshot propertyChange = Clone(baseline);
GattSnapshotCharacteristic changedFfe1 = propertyChange.Services[0].Characteristics.First(c => c.ShortUuid == "FFE1");
changedFfe1.Properties = "Write, WriteWithoutResponse, Notify";
changedFfe1.WritableWithoutResponseAdvertised = true;
List<GattSnapshotDiffRow> propertyRows = GattSnapshotComparer.Compare(Clone(baseline), propertyChange);
Check(propertyRows.Any(r => r.Scope == "CHARACTERISTIC" && r.Field == "Properties" && r.Path.Contains("FFE1")), "characteristic property change detected");
Check(propertyRows.Any(r => r.Field == "WriteWithoutResponse" && r.After == "True"), "individual write property change detected");

GattSnapshot partial = Clone(baseline);
partial.Metadata.GattState = "GATT: PARTIAL / CACHED";
partial.Metadata.ServiceDiscoveryStatus = "GetGattServicesAsync status=AccessDenied statusCode=2 protocol=none";
partial.Services[0].DiscoveryStatus = "AccessDeniedButCached";
List<GattSnapshotDiffRow> partialRows = GattSnapshotComparer.Compare(Clone(baseline), partial);
Check(partialRows.Any(r => r.Scope == "ACCESS" && r.Field == "Service discovery"), "partial discovery access/status change detected");
Check(partialRows.Any(r => r.Scope == "SERVICE" && r.Field == "DiscoveryStatus"), "service discovery status change detected");

GattSnapshot manualRoute = Clone(baseline);
manualRoute.Metadata.ManualWriteUuid = "0000FF31-0000-1000-8000-00805F9B34FB";
manualRoute.Metadata.ManualOverrideActive = true;
List<GattSnapshotDiffRow> mappingRows = GattSnapshotComparer.Compare(Clone(baseline), manualRoute);
Check(mappingRows.Any(r => r.Scope == "MAPPING" && r.Path == "Manual Route" && r.Field == "Write"), "manual route mapping change detected separately");
Check(mappingRows.Any(r => r.Scope == "MAPPING" && r.Field == "OverrideActive"), "manual override flag change detected");

GattSnapshot sourceChange = Clone(baseline);
sourceChange.Services[0].Source = "DISCOVERED";
sourceChange.Services[0].Reused = false;
sourceChange.Services[0].Ownership = "INSPECTOR/DISCOVERED";
List<GattSnapshotDiffRow> sourceRows = GattSnapshotComparer.Compare(Clone(baseline), sourceChange);
Check(sourceRows.Any(r => r.Scope == "SERVICE" && r.Field == "Source"), "cached/fresh source metadata change detected");
Check(sourceRows.Any(r => r.Scope == "SERVICE" && r.Field == "Ownership"), "ownership metadata change detected");

string text = GattSnapshotSerializer.ToText(baseline);
Check(text.Contains("AUTO DETECT BASELINE", StringComparison.Ordinal) && text.Contains("CURRENT MANUAL ROUTE", StringComparison.Ordinal), "human-readable export includes baseline and manual route");
Check(text.Contains("FF31", StringComparison.Ordinal) && text.Contains("FFE1", StringComparison.Ordinal), "human-readable export includes RT950 characteristic detail");

Console.WriteLine();
Console.WriteLine($"GATT SNAPSHOT TESTS: PASS ({passed} checks)");
