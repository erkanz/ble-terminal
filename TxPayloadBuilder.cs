using System.Globalization;
using System.Text;

namespace BLESerialTerminal;

internal enum TxLineEnding
{
    None = 0,
    Lf = 1,
    Cr = 2,
    CrLf = 3
}

internal static class TxPayloadBuilder
{
    public static byte[] Build(string? input, bool hexMode, TxLineEnding lineEnding)
    {
        byte[] body = hexMode
            ? ParseHex(input ?? string.Empty)
            : Encoding.UTF8.GetBytes(input ?? string.Empty);

        byte[] ending = lineEnding switch
        {
            TxLineEnding.Lf => new byte[] { 0x0A },
            TxLineEnding.Cr => new byte[] { 0x0D },
            TxLineEnding.CrLf => new byte[] { 0x0D, 0x0A },
            _ => Array.Empty<byte>()
        };

        if (ending.Length == 0)
            return body;

        byte[] result = new byte[body.Length + ending.Length];
        Buffer.BlockCopy(body, 0, result, 0, body.Length);
        Buffer.BlockCopy(ending, 0, result, body.Length, ending.Length);
        return result;
    }

    public static byte[] ParseHex(string input)
    {
        string compact = new(input
            .Where(c => !char.IsWhiteSpace(c) && c != '-' && c != ':' && c != ',')
            .ToArray());

        if (compact.Length == 0)
            return Array.Empty<byte>();

        if ((compact.Length & 1) != 0)
            throw new FormatException("HEX data must contain an even number of hex digits.");

        byte[] data = new byte[compact.Length / 2];
        for (int i = 0; i < data.Length; i++)
        {
            string pair = compact.Substring(i * 2, 2);
            if (!byte.TryParse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out data[i]))
                throw new FormatException($"Invalid HEX byte: {pair}");
        }

        return data;
    }
}
