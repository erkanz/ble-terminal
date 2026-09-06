using BLESerialTerminal;

int checks = 0;

void Check(bool condition, string name)
{
    checks++;
    if (!condition)
    {
        Console.WriteLine($"FAIL  {name}");
        Environment.ExitCode = 1;
        return;
    }
    Console.WriteLine($"PASS  {name}");
}

var store = new LogStore(1000);
int eventCount = 0;
store.EntryAdded += _ => eventCount++;

for (int i = 0; i < 5000; i++)
{
    LogCategory category = (i % 5) switch
    {
        0 => LogCategory.CONNECTION,
        1 => LogCategory.RX_RAW,
        2 => LogCategory.TX_RAW,
        3 => LogCategory.KISS,
        _ => LogCategory.GATT
    };
    string direction = category == LogCategory.RX_RAW ? "RX" : category == LogCategory.TX_RAW ? "TX" : string.Empty;
    string deviceName = i % 2 == 0 ? "RT-950" : "HMSoft";
    string characteristicName = i % 3 == 0 ? "FFE1" : "6E400003-B5A3-F393-E0A9-E50E24DCCA9E";
    store.Add(category, $"packet {i}", direction, deviceName, characteristicName);
}

IReadOnlyList<LogEntry> snapshot = store.Snapshot();
Check(snapshot.Count == 1000, "bounded log history");
Check(eventCount == 5000, "entry-added event count");
Check(snapshot[0].Sequence == 4001 && snapshot[^1].Sequence == 5000, "monotonic sequence after trim");

store.Add(LogCategory.APRS, "APRS position İstanbul 41.0N", "RX", "RT-950", "FFE1");
store.Add(LogCategory.ERROR, "CCCD FAILED AccessDenied", "", "RT-950", "FFE1", isError: true);
store.Add(LogCategory.WARNING, "RADTEL KISS NOT READY", "", "RT-950", "FFE1", isWarning: true);

snapshot = store.Snapshot();

var rx = LogFilterEngine.Filter(snapshot, new LogFilterCriteria("", "RX_RAW", "", "", "RX", false));
Check(rx.Count > 0 && rx.All(x => x.Category == LogCategory.RX_RAW && x.Direction == "RX"), "category and RX direction filter");

var deviceResults = LogFilterEngine.Filter(snapshot, new LogFilterCriteria("", "ALL", "rt-950", "", "ALL", false));
Check(deviceResults.Count > 0 && deviceResults.All(x => x.Device.Contains("RT-950", StringComparison.OrdinalIgnoreCase)), "device filter");

var characteristicResults = LogFilterEngine.Filter(snapshot, new LogFilterCriteria("", "ALL", "", "ffe1", "ALL", false));
Check(characteristicResults.Count > 0 && characteristicResults.All(x => x.Characteristic.Contains("FFE1", StringComparison.OrdinalIgnoreCase)), "characteristic UUID filter");

var search = LogFilterEngine.Filter(snapshot, new LogFilterCriteria("İstanbul", "ALL", "", "", "ALL", false));
Check(search.Count == 1 && search[0].Category == LogCategory.APRS, "Unicode text search");

var problems = LogFilterEngine.Filter(snapshot, new LogFilterCriteria("", "ALL", "", "", "ALL", true));
Check(problems.Count == 2 && problems.All(x => x.IsError || x.IsWarning), "errors warnings only filter");

var errorClass = LogCategoryClassifier.Classify("TX ERROR: DeviceUnreachable");
Check(errorClass.Category == LogCategory.ERROR && errorClass.Error, "error classifier");

var cccdClass = LogCategoryClassifier.Classify("FFE1 CCCD WRITE RESULT=Success");
Check(cccdClass.Category == LogCategory.CCCD, "CCCD classifier");

var kissClass = LogCategoryClassifier.Classify("KISS RX");
Check(kissClass.Category == LogCategory.KISS, "KISS classifier");

string shortUuid = LogCategoryClassifier.InferCharacteristic("UUID=0000FFE1-0000-1000-8000-00805F9B34FB");
Check(shortUuid == "FFE1", "characteristic UUID inference");

store.Clear();
Check(store.Snapshot().Count == 0, "clear structured log");
store.Add(LogCategory.GATT, "after clear");
Check(store.Snapshot()[0].Sequence > 5000, "sequence remains monotonic after clear");

if (Environment.ExitCode == 0)
    Console.WriteLine($"\nLOG FILTER TESTS: PASS ({checks} checks)");
else
    Console.WriteLine($"\nLOG FILTER TESTS: FAIL ({checks} checks)");
