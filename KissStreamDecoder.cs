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
                    byte[] payload = Unescape(raw.AsSpan(1, raw.Length - 2));
                    completed.Add(new KissFrame(raw, payload));
                }

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
                _raw.Clear();
                _inFrame = false;
            }
        }
        return completed;
    }

    private static byte[] Unescape(ReadOnlySpan<byte> payload)
    {
        var result = new List<byte>(payload.Length);
        for (int i = 0; i < payload.Length; i++)
        {
            byte b = payload[i];
            if (b == Fesc && i + 1 < payload.Length)
            {
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
            }
            result.Add(b);
        }
        return result.ToArray();
    }
}

internal sealed record KissFrame(byte[] Raw, byte[] Payload)
{
    public byte? PortCommand => Payload.Length > 0 ? Payload[0] : null;
}
