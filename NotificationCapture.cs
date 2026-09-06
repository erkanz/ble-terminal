using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace BLESerialTerminal;

internal sealed class NotificationRecord : INotifyPropertyChanged
{
    private int? _kissFrameCount;

    public NotificationRecord(
        long sequence,
        DateTime timestamp,
        string device,
        Guid? serviceUuid,
        Guid characteristicUuid,
        string source,
        bool reused,
        string deliveryMode,
        byte[] data)
    {
        Sequence = sequence;
        Timestamp = timestamp;
        Device = string.IsNullOrWhiteSpace(device) ? "Unnamed BLE device" : device;
        ServiceUuid = serviceUuid;
        CharacteristicUuid = characteristicUuid;
        Source = string.IsNullOrWhiteSpace(source) ? "UNKNOWN" : source;
        Reused = reused;
        DeliveryMode = string.IsNullOrWhiteSpace(deliveryMode) ? "Notify/Indicate" : deliveryMode;
        Data = data.ToArray();
        Hex = BitConverter.ToString(Data).Replace('-', ' ');
        Ascii = PrintableAscii(Data);
    }

    public long Sequence { get; }
    public DateTime Timestamp { get; }
    public string Device { get; }
    public Guid? ServiceUuid { get; }
    public Guid CharacteristicUuid { get; }
    public string Source { get; }
    public bool Reused { get; }
    public string DeliveryMode { get; }
    public byte[] Data { get; }
    public int Length => Data.Length;
    public string Hex { get; }
    public string Ascii { get; }

    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
    public string ServiceText => ServiceUuid.HasValue ? BleUuid.Short(ServiceUuid.Value) : "-";
    public string CharacteristicText => BleUuid.Short(CharacteristicUuid);
    public string SourceText => Reused ? $"{Source} / REUSED" : Source;

    public int? KissFrameCount
    {
        get => _kissFrameCount;
        private set
        {
            if (_kissFrameCount == value)
                return;
            _kissFrameCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KissText));
        }
    }

    public string KissText => KissFrameCount.HasValue ? KissFrameCount.Value.ToString() : "-";

    internal void SetKissFrameCount(int? count) => KissFrameCount = count;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string PrintableAscii(byte[] data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (byte b in data)
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
        return sb.ToString();
    }
}

internal sealed record NotificationCounter(long Notifications, long Bytes);

internal sealed record NotificationStatsSnapshot(
    long Notifications,
    long Bytes,
    IReadOnlyDictionary<string, NotificationCounter> ByCharacteristic);

internal sealed class NotificationCaptureStore
{
    private readonly object _sync = new();
    private readonly List<NotificationRecord> _history = new();
    private readonly Dictionary<string, NotificationCounter> _byCharacteristic = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxHistory;
    private long _sequence;
    private long _notifications;
    private long _bytes;

    public NotificationCaptureStore(int maxHistory)
    {
        if (maxHistory < 1)
            throw new ArgumentOutOfRangeException(nameof(maxHistory));
        _maxHistory = maxHistory;
    }

    public event Action<NotificationRecord>? RecordAdded;
    public event Action? Cleared;

    public NotificationRecord Add(
        string device,
        Guid? serviceUuid,
        Guid characteristicUuid,
        string source,
        bool reused,
        string deliveryMode,
        byte[] data)
    {
        var record = new NotificationRecord(
            Interlocked.Increment(ref _sequence),
            DateTime.Now,
            device,
            serviceUuid,
            characteristicUuid,
            source,
            reused,
            deliveryMode,
            data);

        lock (_sync)
        {
            _history.Add(record);
            if (_history.Count > _maxHistory)
                _history.RemoveRange(0, _history.Count - _maxHistory);

            _notifications++;
            _bytes += record.Length;
            string key = record.CharacteristicText;
            _byCharacteristic.TryGetValue(key, out NotificationCounter? current);
            current ??= new NotificationCounter(0, 0);
            _byCharacteristic[key] = new NotificationCounter(
                current.Notifications + 1,
                current.Bytes + record.Length);
        }

        RecordAdded?.Invoke(record);
        return record;
    }

    public IReadOnlyList<NotificationRecord> Snapshot()
    {
        lock (_sync)
            return _history.ToArray();
    }

    public NotificationStatsSnapshot Stats()
    {
        lock (_sync)
        {
            return new NotificationStatsSnapshot(
                _notifications,
                _bytes,
                new Dictionary<string, NotificationCounter>(_byCharacteristic, StringComparer.OrdinalIgnoreCase));
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _history.Clear();
            _notifications = 0;
            _bytes = 0;
            _byCharacteristic.Clear();
        }
        Cleared?.Invoke();
    }
}

internal sealed record NotificationSubscriptionMetadata(
    Guid ServiceUuid,
    string Source,
    bool Reused,
    string DeliveryMode);
