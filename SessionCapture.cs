using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BLESerialTerminal;

internal static class SessionFormat
{
    public const string Magic = "BLESerialTerminalSession";
    public const int CurrentVersion = 1;
    public const int MinimumSupportedVersion = 1;
}

internal sealed class SessionDocument
{
    public string Format { get; set; } = SessionFormat.Magic;
    public int Version { get; set; } = SessionFormat.CurrentVersion;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public SessionMetadata Metadata { get; set; } = new();
    public List<SessionEventRecord> Events { get; set; } = new();
}

internal sealed class SessionMetadata
{
    public string ApplicationVersion { get; set; } = string.Empty;
    public DateTime CaptureStartedUtc { get; set; }
    public DateTime? CaptureEndedUtc { get; set; }
    public string Device { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string ConnectionState { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public string ServiceUuid { get; set; } = string.Empty;
    public string WriteUuid { get; set; } = string.Empty;
    public string NotifyUuid { get; set; } = string.Empty;
    public bool SameCharacteristic { get; set; }
    public List<SessionGattCharacteristic> RelevantGatt { get; set; } = new();
}

internal sealed class SessionGattCharacteristic
{
    public string Role { get; set; } = string.Empty;
    public string ServiceUuid { get; set; } = string.Empty;
    public string CharacteristicUuid { get; set; } = string.Empty;
    public string Properties { get; set; } = string.Empty;
}

internal sealed class SessionEventRecord
{
    public long Sequence { get; set; }
    public DateTime Timestamp { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
    public string Characteristic { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsWarning { get; set; }
    public bool IsError { get; set; }
    public int DataLength { get; set; }
    public string DataBase64 { get; set; } = string.Empty;
    public string DataHex { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasData => DataLength > 0 && !string.IsNullOrWhiteSpace(DataBase64);

    public byte[] GetData()
    {
        if (string.IsNullOrWhiteSpace(DataBase64))
            return Array.Empty<byte>();
        try
        {
            byte[] data = Convert.FromBase64String(DataBase64);
            if (DataLength != 0 && data.Length != DataLength)
                throw new InvalidDataException($"Session event data length mismatch: expected {DataLength}, decoded {data.Length}.");
            return data;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Session event contains invalid Base64 data.", ex);
        }
    }

    public static SessionEventRecord FromLogEntry(LogEntry entry)
    {
        byte[] data = entry.Data ?? Array.Empty<byte>();
        return new SessionEventRecord
        {
            Sequence = entry.Sequence,
            Timestamp = entry.Timestamp,
            Category = entry.CategoryText,
            Direction = entry.Direction,
            Device = entry.Device,
            Characteristic = entry.Characteristic,
            Message = entry.Message,
            IsWarning = entry.IsWarning,
            IsError = entry.IsError,
            DataLength = data.Length,
            DataBase64 = data.Length == 0 ? string.Empty : Convert.ToBase64String(data),
            DataHex = data.Length == 0 ? string.Empty : BitConverter.ToString(data).Replace('-', ' ')
        };
    }
}

internal sealed class SessionRecorder : IDisposable
{
    private readonly LogStore _store;
    private readonly object _sync = new();
    private readonly List<SessionEventRecord> _events = new();
    private SessionMetadata _metadata = new();
    private bool _recording;
    private bool _disposed;

    public SessionRecorder(LogStore store)
    {
        _store = store;
    }

    public bool IsRecording
    {
        get { lock (_sync) return _recording; }
    }

    public int EventCount
    {
        get { lock (_sync) return _events.Count; }
    }

    public event Action<int>? EventCountChanged;
    public event Action<bool>? RecordingStateChanged;

    public void Start(SessionMetadata metadata)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (metadata == null)
            throw new ArgumentNullException(nameof(metadata));

        lock (_sync)
        {
            if (_recording)
                throw new InvalidOperationException("Session capture is already recording.");

            _events.Clear();
            _metadata = CloneMetadata(metadata);
            _metadata.CaptureStartedUtc = DateTime.UtcNow;
            _metadata.CaptureEndedUtc = null;
            _recording = true;
            _store.EntryAdded += Store_EntryAdded;
        }

        EventCountChanged?.Invoke(0);
        RecordingStateChanged?.Invoke(true);
    }

    public void Stop()
    {
        bool changed;
        lock (_sync)
        {
            changed = _recording;
            if (!_recording)
                return;
            _recording = false;
            _metadata.CaptureEndedUtc = DateTime.UtcNow;
            _store.EntryAdded -= Store_EntryAdded;
        }
        if (changed)
            RecordingStateChanged?.Invoke(false);
    }

    public SessionDocument Snapshot()
    {
        lock (_sync)
        {
            SessionMetadata metadata = CloneMetadata(_metadata);
            if (_recording)
                metadata.CaptureEndedUtc = DateTime.UtcNow;

            return new SessionDocument
            {
                Format = SessionFormat.Magic,
                Version = SessionFormat.CurrentVersion,
                CreatedUtc = DateTime.UtcNow,
                Metadata = metadata,
                Events = _events.Select(CloneEvent).ToList()
            };
        }
    }

    private void Store_EntryAdded(LogEntry entry)
    {
        int count;
        lock (_sync)
        {
            if (!_recording)
                return;
            _events.Add(SessionEventRecord.FromLogEntry(entry));
            count = _events.Count;
        }
        EventCountChanged?.Invoke(count);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _disposed = true;
    }

    private static SessionMetadata CloneMetadata(SessionMetadata value) => new()
    {
        ApplicationVersion = value.ApplicationVersion,
        CaptureStartedUtc = value.CaptureStartedUtc,
        CaptureEndedUtc = value.CaptureEndedUtc,
        Device = value.Device,
        Address = value.Address,
        ConnectionState = value.ConnectionState,
        Profile = value.Profile,
        ServiceUuid = value.ServiceUuid,
        WriteUuid = value.WriteUuid,
        NotifyUuid = value.NotifyUuid,
        SameCharacteristic = value.SameCharacteristic,
        RelevantGatt = value.RelevantGatt.Select(x => new SessionGattCharacteristic
        {
            Role = x.Role,
            ServiceUuid = x.ServiceUuid,
            CharacteristicUuid = x.CharacteristicUuid,
            Properties = x.Properties
        }).ToList()
    };

    private static SessionEventRecord CloneEvent(SessionEventRecord value) => new()
    {
        Sequence = value.Sequence,
        Timestamp = value.Timestamp,
        Category = value.Category,
        Direction = value.Direction,
        Device = value.Device,
        Characteristic = value.Characteristic,
        Message = value.Message,
        IsWarning = value.IsWarning,
        IsError = value.IsError,
        DataLength = value.DataLength,
        DataBase64 = value.DataBase64,
        DataHex = value.DataHex
    };
}

internal static class SessionSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(SessionDocument document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static SessionDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Session file is empty.");

        SessionDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SessionDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Session file is not valid JSON.", ex);
        }

        if (document == null)
            throw new InvalidDataException("Session file did not contain a document.");

        Validate(document);
        return document;
    }

    public static void Save(string path, SessionDocument document)
    {
        string json = Serialize(document);
        System.IO.File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
    }

    public static SessionDocument Load(string path) => Deserialize(System.IO.File.ReadAllText(path));

    public static void Validate(SessionDocument document)
    {
        if (!string.Equals(document.Format, SessionFormat.Magic, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported session format '{document.Format}'.");
        if (document.Version < SessionFormat.MinimumSupportedVersion)
            throw new NotSupportedException($"Session version {document.Version} is too old. Minimum supported version is {SessionFormat.MinimumSupportedVersion}.");
        if (document.Version > SessionFormat.CurrentVersion)
            throw new NotSupportedException($"Session version {document.Version} is newer than this application supports ({SessionFormat.CurrentVersion}).");

        document.Metadata ??= new SessionMetadata();
        document.Events ??= new List<SessionEventRecord>();
        document.Metadata.RelevantGatt ??= new List<SessionGattCharacteristic>();

        long previous = long.MinValue;
        foreach (SessionEventRecord record in document.Events.OrderBy(x => x.Sequence))
        {
            if (record.Sequence < 0)
                throw new InvalidDataException("Session event sequence cannot be negative.");
            if (record.Sequence == previous)
                throw new InvalidDataException($"Duplicate session event sequence {record.Sequence}.");
            previous = record.Sequence;
            if (record.DataLength < 0)
                throw new InvalidDataException("Session event DataLength cannot be negative.");
            if (record.DataLength > 0)
                _ = record.GetData();
        }
    }
}
