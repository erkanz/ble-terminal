namespace BLESerialTerminal;

internal sealed record ReplayDecodedPacket(
    DateTime Timestamp,
    string Characteristic,
    KissFrame Kiss,
    Ax25Packet? Ax25,
    AprsPacket? Aprs,
    string DecodeError)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
    public string KissText => Kiss.PortCommand.HasValue ? $"P{Kiss.Port ?? 0}/C{Kiss.Command ?? 0}" : "--";
    public string Source => Ax25?.Source.Display ?? string.Empty;
    public string Destination => Ax25?.Destination.Display ?? string.Empty;
    public string Path => Ax25?.Path ?? string.Empty;
    public string Type => Aprs?.Category ?? (Ax25 != null ? $"AX.25 {Ax25.FrameType}" : Kiss.IsDataFrame ? "KISS data" : "KISS command");
    public string Summary => Aprs?.Summary ??
        (Ax25 != null
            ? $"{Ax25.Source.Display}>{Ax25.Destination.Display} {Ax25.FrameType}"
            : !string.IsNullOrWhiteSpace(DecodeError)
                ? DecodeError
                : $"KISS port={Kiss.Port?.ToString() ?? "?"} command={Kiss.Command?.ToString() ?? "?"}");
}

internal sealed class SessionReplayProcessor
{
    private readonly Dictionary<string, KissStreamDecoder> _kissDecoders = new(StringComparer.OrdinalIgnoreCase);

    public long ProcessedEvents { get; private set; }
    public long RawRxEvents { get; private set; }
    public long RawRxBytes { get; private set; }
    public long KissFrames { get; private set; }

    public IReadOnlyList<ReplayDecodedPacket> Process(SessionEventRecord record)
    {
        ProcessedEvents++;
        if (!record.Category.Equals(LogCategory.RX_RAW.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !record.Direction.Equals("RX", StringComparison.OrdinalIgnoreCase) ||
            !record.HasData)
            return Array.Empty<ReplayDecodedPacket>();

        byte[] data = record.GetData();
        RawRxEvents++;
        RawRxBytes += data.Length;

        string characteristic = string.IsNullOrWhiteSpace(record.Characteristic) ? "UNKNOWN" : record.Characteristic;
        if (!_kissDecoders.TryGetValue(characteristic, out KissStreamDecoder? decoder))
        {
            decoder = new KissStreamDecoder();
            _kissDecoders[characteristic] = decoder;
        }

        var results = new List<ReplayDecodedPacket>();
        foreach (KissFrame frame in decoder.Push(data))
        {
            KissFrames++;
            results.Add(Decode(record.Timestamp, characteristic, frame));
        }
        return results;
    }

    public void Reset()
    {
        foreach (KissStreamDecoder decoder in _kissDecoders.Values)
            decoder.Reset();
        _kissDecoders.Clear();
        ProcessedEvents = 0;
        RawRxEvents = 0;
        RawRxBytes = 0;
        KissFrames = 0;
    }

    private static ReplayDecodedPacket Decode(DateTime timestamp, string characteristic, KissFrame kiss)
    {
        Ax25Packet? ax25 = null;
        AprsPacket? aprs = null;
        string error = string.Empty;

        if (!kiss.IsDataFrame)
        {
            error = $"KISS command {kiss.Command?.ToString() ?? "?"}; no AX.25 decode attempted";
        }
        else if (!Ax25Decoder.TryDecode(kiss.Data, out ax25, out error))
        {
            // Preserve raw KISS frame and decode error.
        }
        else
        {
            aprs = AprsDecoder.Decode(ax25!);
        }

        return new ReplayDecodedPacket(timestamp, characteristic, kiss, ax25, aprs, error);
    }
}
