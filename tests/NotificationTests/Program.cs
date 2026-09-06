using BLESerialTerminal;

int checks = 0;

void Check(bool condition, string name)
{
    if (!condition)
    {
        Console.Error.WriteLine($"FAIL  {name}");
        Environment.ExitCode = 1;
        throw new InvalidOperationException(name);
    }

    checks++;
    Console.WriteLine($"PASS  {name}");
}

Guid service = Guid.Parse("0000FFE0-0000-1000-8000-00805F9B34FB");
Guid ffe1 = Guid.Parse("0000FFE1-0000-1000-8000-00805F9B34FB");
Guid ff31 = Guid.Parse("0000FF31-0000-1000-8000-00805F9B34FB");

var store = new NotificationCaptureStore(1000);
long eventCount = 0;
store.RecordAdded += _ => Interlocked.Increment(ref eventCount);

for (int i = 0; i < 20000; i++)
{
    Guid characteristic = (i & 1) == 0 ? ffe1 : ff31;
    store.Add(
        "synthetic",
        service,
        characteristic,
        "TEST",
        reused: false,
        deliveryMode: "Notify",
        data: new byte[] { (byte)i, 0xC0, 0x0A, 0xFF });
}

IReadOnlyList<NotificationRecord> history = store.Snapshot();
NotificationStatsSnapshot stats = store.Stats();

Check(history.Count == 1000, "bounded capture history");
Check(stats.Notifications == 20000, "high-rate notification counter");
Check(stats.Bytes == 80000, "high-rate byte counter");
Check(eventCount == 20000, "record-added event count");
Check(stats.ByCharacteristic.TryGetValue("FFE1", out NotificationCounter? ffe1Counter) && ffe1Counter.Notifications == 10000, "FFE1 per-characteristic counter");
Check(stats.ByCharacteristic.TryGetValue("FF31", out NotificationCounter? ff31Counter) && ff31Counter.Notifications == 10000, "FF31 per-characteristic counter");
Check(history.Zip(history.Skip(1), (a, b) => b.Sequence > a.Sequence).All(x => x), "monotonic sequence numbers");

NotificationRecord ascii = store.Add(
    "synthetic",
    service,
    ffe1,
    "AUTO-DETECT",
    reused: true,
    deliveryMode: "Notify",
    data: new byte[] { (byte)'A', 0x00, (byte)'Z' });
Check(ascii.Ascii == "A.Z", "ASCII-safe rendering");
Check(ascii.SourceText.Contains("REUSED", StringComparison.Ordinal), "source/reused metadata");

ascii.SetKissFrameCount(2);
Check(ascii.KissFrameCount == 2 && ascii.KissText == "2", "related KISS frame count");

long lastSequence = ascii.Sequence;
bool cleared = false;
store.Cleared += () => cleared = true;
store.Clear();
Check(cleared, "clear event");
Check(store.Snapshot().Count == 0, "clear removes capture history");
NotificationStatsSnapshot clearedStats = store.Stats();
Check(clearedStats.Notifications == 0 && clearedStats.Bytes == 0 && clearedStats.ByCharacteristic.Count == 0, "clear resets counters");

NotificationRecord afterClear = store.Add("synthetic", service, ffe1, "TEST", false, "Indicate", new byte[] { 1 });
Check(afterClear.Sequence > lastSequence, "sequence remains monotonic after clear");
Check(afterClear.DeliveryMode == "Indicate", "notification/indication source metadata");

Console.WriteLine();
Console.WriteLine($"NOTIFICATION TESTS: PASS ({checks} checks)");
