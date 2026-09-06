namespace BLESerialTerminal;

internal enum LogCategory
{
    CONNECTION,
    DISCOVERY,
    GATT,
    CCCD,
    RX_RAW,
    TX_RAW,
    KISS,
    AX25,
    APRS,
    INSPECTOR,
    WARNING,
    ERROR
}

internal sealed record LogEntry(
    long Sequence,
    DateTime Timestamp,
    LogCategory Category,
    string Direction,
    string Device,
    string Characteristic,
    string Message,
    bool IsWarning,
    bool IsError)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
    public string CategoryText => Category.ToString();
    public string LevelText => IsError ? "ERROR" : IsWarning ? "WARNING" : string.Empty;
}

internal sealed class LogStore
{
    private readonly object _sync = new();
    private readonly List<LogEntry> _history = new();
    private readonly int _maxHistory;
    private long _sequence;

    public LogStore(int maxHistory = 50000)
    {
        if (maxHistory < 1)
            throw new ArgumentOutOfRangeException(nameof(maxHistory));
        _maxHistory = maxHistory;
    }

    public event Action<LogEntry>? EntryAdded;
    public event Action? Cleared;

    public LogEntry Add(
        LogCategory category,
        string message,
        string direction = "",
        string device = "",
        string characteristic = "",
        bool isWarning = false,
        bool isError = false,
        DateTime? timestamp = null)
    {
        var entry = new LogEntry(
            Interlocked.Increment(ref _sequence),
            timestamp ?? DateTime.Now,
            category,
            direction?.Trim().ToUpperInvariant() ?? string.Empty,
            string.IsNullOrWhiteSpace(device) ? "-" : device.Trim(),
            string.IsNullOrWhiteSpace(characteristic) ? "-" : characteristic.Trim().ToUpperInvariant(),
            message ?? string.Empty,
            isWarning,
            isError);

        lock (_sync)
        {
            _history.Add(entry);
            if (_history.Count > _maxHistory)
                _history.RemoveRange(0, _history.Count - _maxHistory);
        }

        EntryAdded?.Invoke(entry);
        return entry;
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_sync)
            return _history.ToArray();
    }

    public void Clear()
    {
        lock (_sync)
            _history.Clear();
        Cleared?.Invoke();
    }
}

internal static class AppLogHub
{
    internal static LogStore Store { get; } = new(50000);
}

internal static class LogCategoryClassifier
{
    public static (LogCategory Category, bool Warning, bool Error) Classify(string message)
    {
        string m = message ?? string.Empty;
        string u = m.ToUpperInvariant();

        bool error = u.Contains(" ERROR") || u.StartsWith("ERROR") || u.Contains(" FAILED") ||
                     u.StartsWith("FAILED") || u.Contains("EXCEPTION=") || u.Contains(" EXCEPTION") ||
                     u.Contains("HRESULT=") && u.Contains("FAILED");
        bool warning = !error && (u.Contains("WARNING") || u.Contains("NOT READY") ||
                                  u.Contains("UNAVAILABLE") || u.Contains("MALFORMED"));

        if (error)
            return (LogCategory.ERROR, Warning: false, Error: true);
        if (warning)
            return (LogCategory.WARNING, Warning: true, Error: false);
        if (u.Contains("KISS"))
            return (LogCategory.KISS, false, false);
        if (u.Contains("AX25") || u.Contains("AX.25"))
            return (LogCategory.AX25, false, false);
        if (u.Contains("APRS"))
            return (LogCategory.APRS, false, false);
        if (u.Contains("CCCD"))
            return (LogCategory.CCCD, false, false);
        if (u.Contains("INSPECTOR"))
            return (LogCategory.INSPECTOR, false, false);
        if (u.Contains("DISCOVER") || u.Contains("AUTO GATT") || u.Contains("SERVICE=") ||
            u.Contains("CHARACTERISTIC"))
            return (LogCategory.DISCOVERY, false, false);
        if (u.Contains("CONNECTED") || u.Contains("DISCONNECTED") || u.Contains("RECONNECT") ||
            u.StartsWith("BLE_") || u.StartsWith("STATE BLE="))
            return (LogCategory.CONNECTION, false, false);
        return (LogCategory.GATT, false, false);
    }

    public static string InferCharacteristic(string message)
    {
        string m = message ?? string.Empty;
        foreach (string marker in new[] { "uuid=", "UUID=", "SOURCE=", "notify=", "write=", "rx=", "tx=", "char=" })
        {
            int start = m.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                continue;
            start += marker.Length;
            while (start < m.Length && char.IsWhiteSpace(m[start])) start++;
            int end = start;
            while (end < m.Length && !char.IsWhiteSpace(m[end]) && m[end] != '[' && m[end] != ',' && m[end] != ';') end++;
            string token = m[start..end].Trim().Trim('(', ')', ':');
            if (BleUuid.TryParse(token, out Guid uuid))
                return BleUuid.Short(uuid);
        }
        return string.Empty;
    }
}
