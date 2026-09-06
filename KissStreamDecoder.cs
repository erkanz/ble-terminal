namespace BLESerialTerminal;

internal sealed class KissStreamDecoder
{
    private const byte Fend = 0xC0;
    private const byte Fesc = 0xDB;
    private const byte Tfend = 0xDC;
    private const byte Tfesc = 0xDD;
    private const int MaxFrameBytes = 65536;

    private readonly List<byte> _raw = new();
    private bool _inFrame;

    internal static event Action<KissStreamDecoder, KissFrame>? AnyFrameCompleted;

    public IEnumerable<KissFrame> Push(ReadOnlySpan<byte> chunk)
    {
        var completed = new List<KissFrame>();
        foreach (byte value in chunk)
        {
            if (value == Fend)
            {
                if (_inFrame && _raw.Count > 1)
                {
                    _raw.Add(Fend);
                    byte[] raw = _raw.ToArray();
                    UnescapeResult decoded = Unescape(raw.AsSpan(1, raw.Length - 2));
                    var frame = new KissFrame(raw, decoded.Bytes, decoded.Warnings);
                    completed.Add(frame);
                    AnyFrameCompleted?.Invoke(this, frame);
                }

                // A FEND closes the current frame and simultaneously starts the next one.
                // Repeated FEND bytes therefore do not produce empty frames.
                _raw.Clear();
                _raw.Add(Fend);
                _inFrame = true;
                continue;
            }

            if (!_inFrame)
                continue;

            _raw.Add(value);
            if (_raw.Count > MaxFrameBytes)
            {
                // Drop an unterminated runaway frame without affecting future FEND sync.
                _raw.Clear();
                _inFrame = false;
            }
        }
        return completed;
    }

    public void Reset()
    {
        _raw.Clear();
        _inFrame = false;
    }

    private static UnescapeResult Unescape(ReadOnlySpan<byte> payload)
    {
        var result = new List<byte>(payload.Length);
        var warnings = new List<string>();

        for (int i = 0; i < payload.Length; i++)
        {
            byte b = payload[i];
            if (b != Fesc)
            {
                result.Add(b);
                continue;
            }

            if (i + 1 >= payload.Length)
            {
                warnings.Add("Trailing FESC without an escape code");
                result.Add(Fesc);
                continue;
            }

            byte next = payload[i + 1];
            if (next == Tfend)
            {
                result.Add(Fend);
                i++;
                continue;
            }

            if (next == Tfesc)
            {
                result.Add(Fesc);
                i++;
                continue;
            }

            warnings.Add($"Unknown KISS escape sequence DB {next:X2}");
            // Preserve the original bytes so malformed traffic is never hidden.
            result.Add(Fesc);
            result.Add(next);
            i++;
        }

        return new UnescapeResult(result.ToArray(), warnings);
    }

    private sealed record UnescapeResult(byte[] Bytes, IReadOnlyList<string> Warnings);
}

internal sealed record KissFrame(byte[] Raw, byte[] Payload, IReadOnlyList<string> Warnings)
{
    public byte? PortCommand => Payload.Length > 0 ? Payload[0] : null;
    public int? Port => PortCommand.HasValue ? (PortCommand.Value >> 4) & 0x0F : null;
    public int? Command => PortCommand.HasValue ? PortCommand.Value & 0x0F : null;
    public bool IsDataFrame => Command == 0;
    public bool IsMalformed => Warnings.Count > 0;

    public byte[] Data
    {
        get
        {
            if (Payload.Length <= 1)
                return Array.Empty<byte>();

            byte[] data = new byte[Payload.Length - 1];
            System.Buffer.BlockCopy(Payload, 1, data, 0, data.Length);
            return data;
        }
    }
}
