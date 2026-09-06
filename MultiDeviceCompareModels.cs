namespace BLESerialTerminal;

internal sealed record MultiDeviceSessionSnapshot(
    string SessionId,
    string DeviceName,
    string Address,
    string State,
    string Profile,
    string ServiceUuid,
    string WriteUuid,
    string NotifyUuid,
    bool SameCharacteristic,
    bool CccdReady,
    long Notifications,
    long RxBytes,
    long KissFrames,
    long Ax25Frames,
    long AprsPackets,
    long TxBytes,
    string LastSource,
    string LastDestination,
    string LastPath,
    string LastAprsType,
    string LastSummary,
    string LastError)
{
    public static MultiDeviceSessionSnapshot Empty(string sessionId, string deviceName, string address) => new(
        sessionId,
        deviceName,
        address,
        "DISCONNECTED",
        "-",
        "-",
        "-",
        "-",
        false,
        false,
        0, 0, 0, 0, 0, 0,
        "-", "-", "-", "-", "-", string.Empty);
}

internal sealed record MultiDeviceRawRecord(
    long Sequence,
    DateTime Timestamp,
    string SessionId,
    string DeviceName,
    string Direction,
    string Characteristic,
    byte[] Data)
{
    public int Length => Data.Length;
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
    public string Hex => BitConverter.ToString(Data).Replace('-', ' ');
}

internal sealed record MultiDeviceComparisonLine(string Field, string A, string B, bool Different)
{
    public string Marker => Different ? "≠" : "=";
}

internal static class MultiDeviceComparisonEngine
{
    public static IReadOnlyList<MultiDeviceComparisonLine> Compare(
        MultiDeviceSessionSnapshot a,
        MultiDeviceSessionSnapshot b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var lines = new List<MultiDeviceComparisonLine>();
        Add(lines, "Device", a.DeviceName, b.DeviceName);
        Add(lines, "Address", a.Address, b.Address);
        Add(lines, "State", a.State, b.State);
        Add(lines, "Profile", a.Profile, b.Profile);
        Add(lines, "Service", a.ServiceUuid, b.ServiceUuid);
        Add(lines, "Write", a.WriteUuid, b.WriteUuid);
        Add(lines, "Notify", a.NotifyUuid, b.NotifyUuid);
        Add(lines, "Same characteristic", YesNo(a.SameCharacteristic), YesNo(b.SameCharacteristic));
        Add(lines, "CCCD ready", YesNo(a.CccdReady), YesNo(b.CccdReady));
        Add(lines, "Notifications", a.Notifications.ToString(), b.Notifications.ToString());
        Add(lines, "RX bytes", a.RxBytes.ToString(), b.RxBytes.ToString());
        Add(lines, "KISS frames", a.KissFrames.ToString(), b.KissFrames.ToString());
        Add(lines, "AX.25 frames", a.Ax25Frames.ToString(), b.Ax25Frames.ToString());
        Add(lines, "APRS packets", a.AprsPackets.ToString(), b.AprsPackets.ToString());
        Add(lines, "TX bytes", a.TxBytes.ToString(), b.TxBytes.ToString());
        Add(lines, "Last source", a.LastSource, b.LastSource);
        Add(lines, "Last destination", a.LastDestination, b.LastDestination);
        Add(lines, "Last path", a.LastPath, b.LastPath);
        Add(lines, "Last APRS type", a.LastAprsType, b.LastAprsType);
        Add(lines, "Last summary", a.LastSummary, b.LastSummary);
        Add(lines, "Last error", a.LastError, b.LastError);
        return lines;
    }

    private static void Add(List<MultiDeviceComparisonLine> lines, string field, string a, string b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        lines.Add(new MultiDeviceComparisonLine(
            field,
            a,
            b,
            !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)));
    }

    private static string YesNo(bool value) => value ? "YES" : "NO";
}

internal sealed class MultiDeviceDiscoveryItem
{
    public ulong Address { get; init; }
    public string Name { get; set; } = "Unnamed BLE device";
    public short Rssi { get; set; }
    public DateTime LastSeen { get; set; }
    public string AddressText => FormatAddress(Address);
    public string RssiText => $"{Rssi} dBm";
    public string LastSeenText => LastSeen.ToString("HH:mm:ss");

    public static string FormatAddress(ulong address)
    {
        string h = address.ToString("X12");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => h.Substring(i * 2, 2)));
    }
}
