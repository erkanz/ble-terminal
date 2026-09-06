using BLESerialTerminal;

internal static class Program
{
    private static int _checks;

    public static int Main()
    {
        try
        {
            MultiDeviceSessionSnapshot a = Snapshot(
                "A1", "RT950-A", "AA:BB:CC:DD:EE:01", "READY", "RADTEL_RT950_KISS",
                "FFE0", "FFE1", "FFE1", true, true,
                10, 120, 3, 3, 2, 40,
                "N0CALL-1", "APRS", "WIDE1-1", "Position", "A position", string.Empty);

            MultiDeviceSessionSnapshot b = Snapshot(
                "B1", "RT950-B", "AA:BB:CC:DD:EE:02", "READY", "RADTEL_RT950_KISS",
                "FFE0", "FFE1", "FFE1", true, true,
                12, 140, 4, 4, 3, 60,
                "N0CALL-2", "APRS", "WIDE1-1", "Position", "B position", string.Empty);

            IReadOnlyList<MultiDeviceComparisonLine> diff = MultiDeviceComparisonEngine.Compare(a, b);
            Check(diff.Count == 21, "comparison emits all expected fields");
            Check(Find(diff, "Profile").Different == false, "equal profile is marked equal");
            Check(Find(diff, "Profile").Marker == "=", "equal marker is stable");
            Check(Find(diff, "Address").Different, "different address detected");
            Check(Find(diff, "Address").Marker == "≠", "difference marker is stable");
            Check(Find(diff, "RX bytes").A == "120" && Find(diff, "RX bytes").B == "140", "counter values preserved");
            Check(Find(diff, "CCCD ready").A == "YES" && Find(diff, "CCCD ready").B == "YES", "boolean rendering is deterministic");
            Check(Find(diff, "Last source").Different, "last packet identity difference detected");
            Check(Find(diff, "Last summary").A == "A position" && Find(diff, "Last summary").B == "B position", "packet summaries preserved");

            IReadOnlyList<MultiDeviceComparisonLine> same = MultiDeviceComparisonEngine.Compare(a, a);
            Check(same.All(x => !x.Different), "self comparison has no differences");

            MultiDeviceSessionSnapshot empty = MultiDeviceSessionSnapshot.Empty("EMPTY", "No Device", "00:00:00:00:00:00");
            Check(empty.State == "DISCONNECTED", "empty snapshot state is disconnected");
            Check(empty.ServiceUuid == "-" && empty.WriteUuid == "-" && empty.NotifyUuid == "-", "empty snapshot has no live GATT route");
            Check(!empty.CccdReady && empty.RxBytes == 0 && empty.TxBytes == 0, "empty snapshot counters and CCCD are clear");

            Check(MultiDeviceDiscoveryItem.FormatAddress(0xAABBCCDDEEFF) == "AA:BB:CC:DD:EE:FF", "BLE address formatter is stable");
            Check(MultiDeviceDiscoveryItem.FormatAddress(0x000000000001) == "00:00:00:00:00:01", "BLE address formatter preserves leading zeros");

            Console.WriteLine();
            Console.WriteLine($"MULTI-DEVICE COMPARE TESTS: PASS ({_checks} checks)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("MULTI-DEVICE COMPARE TESTS: FAIL");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static MultiDeviceSessionSnapshot Snapshot(
        string id, string device, string address, string state, string profile,
        string service, string write, string notify, bool sameCharacteristic, bool cccd,
        long notifications, long rxBytes, long kissFrames, long ax25Frames, long aprsPackets, long txBytes,
        string lastSource, string lastDestination, string lastPath, string lastAprsType, string lastSummary, string lastError) =>
        new(id, device, address, state, profile, service, write, notify, sameCharacteristic, cccd,
            notifications, rxBytes, kissFrames, ax25Frames, aprsPackets, txBytes,
            lastSource, lastDestination, lastPath, lastAprsType, lastSummary, lastError);

    private static MultiDeviceComparisonLine Find(IReadOnlyList<MultiDeviceComparisonLine> lines, string field) =>
        lines.Single(x => x.Field == field);

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
        _checks++;
        Console.WriteLine($"PASS  {description}");
    }
}
