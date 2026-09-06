namespace BLESerialTerminal;

internal sealed class OptionalFeatureSettings
{
    public int Version { get; set; } = 1;
    public bool Rt950ToolsEnabled { get; set; }
    public bool Rt950AutoUnlockOnConnect { get; set; }
    public bool KissToolsEnabled { get; set; }
    public bool KissRxDecoderEnabled { get; set; } = true;
    public bool KissShowRawFrames { get; set; } = true;
    public bool KissShowDecoded { get; set; } = true;
}

internal enum Rt950UnlockState
{
    Disconnected,
    Connected,
    ServicesDiscovered,
    Ffe1NotifyEnabling,
    Ffe1NotifyReady,
    Ff31UnlockWrite,
    WaitUnlockResponse,
    Ready,
    Failed
}

internal static class Rt950Protocol
{
    public static readonly byte[] UnlockFrame =
    {
        0x3F, 0x3F, 0x3F, 0x3F, 0x02, 0x2E, 0x17, 0x1D, 0x5E, 0x57,
        0x25, 0x2F, 0x57, 0x13, 0x62, 0x56, 0x04, 0x4B, 0x23, 0x42
    };

    public static readonly byte[] OemHandshake =
    {
        0x50, 0x52, 0x4F, 0x47, 0x52, 0x41, 0x4D, 0x42, 0x54, 0x39,
        0x30, 0x30, 0x30, 0x55
    };

    public static readonly byte[] ModelQuery = { 0x4D };

    public static bool IsUnlockFrame(ReadOnlySpan<byte> data) => data.SequenceEqual(UnlockFrame);

    public static bool IsUnlockResponse(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && data.Length <= 32 && data[0] == 0x21;

    public static bool IsOemAck(ReadOnlySpan<byte> data) => data.Length == 1 && data[0] == 0x06;
}

internal static class KissFrameBuilder
{
    private const byte Fend = 0xC0;
    private const byte Fesc = 0xDB;
    private const byte Tfend = 0xDC;
    private const byte Tfesc = 0xDD;

    public static byte[] BuildDataFrame(ReadOnlySpan<byte> ax25Payload, byte port = 0)
    {
        if (port > 0x0F)
            throw new ArgumentOutOfRangeException(nameof(port), "KISS port must be 0 through 15.");

        byte portCommand = (byte)(port << 4);
        var output = new List<byte>(ax25Payload.Length + 4) { Fend };
        AppendEscaped(output, portCommand);
        foreach (byte value in ax25Payload)
            AppendEscaped(output, value);
        output.Add(Fend);
        return output.ToArray();
    }

    private static void AppendEscaped(List<byte> output, byte value)
    {
        if (value == Fend)
        {
            output.Add(Fesc);
            output.Add(Tfend);
        }
        else if (value == Fesc)
        {
            output.Add(Fesc);
            output.Add(Tfesc);
        }
        else
        {
            output.Add(value);
        }
    }
}
