using System.Text;

namespace BLESerialTerminal;

internal static class Rt950Rtx1Protocol
{
    public const string ServiceUuid = "FFE0";
    public const string HandshakeCharacteristicUuid = "FF31";
    public const string Rtx1CharacteristicUuid = "FFE1";
    public const int ChunkSize = 20;
    public const string FirmwareExpected = "RT950_OEM_V029_BLE_HOST_TX_RTX1_DIAG_v0.8b";
    public const string Source = "TEST-1";
    public const string Destination = "APRS";

    public static readonly byte[] OemHandshake =
    {
        0x3F, 0x3F, 0x3F, 0x3F, 0x02, 0x2E, 0x17, 0x1D, 0x5E, 0x57,
        0x25, 0x2F, 0x57, 0x13, 0x62, 0x56, 0x04, 0x4B, 0x23, 0x42
    };

    private static readonly byte[] Rtx1Prefix = Encoding.ASCII.GetBytes("RTX1");

    public static string InformationText(int counter)
    {
        if (counter < 1 || counter > 9999)
            throw new ArgumentOutOfRangeException(nameof(counter), "RTX1 test counter must be 1 through 9999.");
        return $">RT950 RTX1 BLE TX TEST #{counter:D4}";
    }

    public static byte[] EncodeAddress(string callsign, int ssid, bool last)
    {
        if (string.IsNullOrWhiteSpace(callsign))
            throw new ArgumentException("AX.25 callsign is required.", nameof(callsign));
        if (ssid is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(ssid), "AX.25 SSID must be 0 through 15.");

        string normalized = callsign.Trim().ToUpperInvariant();
        if (normalized.Length > 6)
            throw new ArgumentException("AX.25 callsign may not exceed 6 characters.", nameof(callsign));
        if (normalized.Any(c => c > 0x7F))
            throw new ArgumentException("AX.25 callsign must contain ASCII characters only.", nameof(callsign));

        normalized = normalized.PadRight(6, ' ');
        var result = new byte[7];
        for (int i = 0; i < 6; i++)
            result[i] = (byte)(normalized[i] << 1);

        // AX.25 SSID octet: reserved bits set, SSID in bits 1..4, extension bit in bit 0.
        result[6] = (byte)(0x60 | (ssid << 1) | (last ? 0x01 : 0x00));
        return result;
    }

    public static byte[] BuildAx25Raw(int counter)
    {
        byte[] destination = EncodeAddress(Destination, 0, last: false);
        byte[] source = EncodeAddress("TEST", 1, last: true);
        byte[] information = Encoding.ASCII.GetBytes(InformationText(counter));

        var raw = new byte[destination.Length + source.Length + 2 + information.Length];
        int offset = 0;
        Buffer.BlockCopy(destination, 0, raw, offset, destination.Length);
        offset += destination.Length;
        Buffer.BlockCopy(source, 0, raw, offset, source.Length);
        offset += source.Length;
        raw[offset++] = 0x03;
        raw[offset++] = 0xF0;
        Buffer.BlockCopy(information, 0, raw, offset, information.Length);
        return raw;
    }

    public static ushort CalculateFcs(ReadOnlySpan<byte> frame)
    {
        ushort crc = 0xFFFF;
        foreach (byte value in frame)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x0001) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1);
        }
        return (ushort)~crc;
    }

    public static byte[] FcsWireBytes(ushort fcs) =>
        new[] { (byte)(fcs & 0xFF), (byte)(fcs >> 8) };

    public static bool VerifyFcs(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> wireFcs)
    {
        if (wireFcs.Length != 2)
            return false;
        ushort calculated = CalculateFcs(frame);
        return wireFcs[0] == (byte)(calculated & 0xFF) && wireFcs[1] == (byte)(calculated >> 8);
    }

    public static byte[] BuildRtx1Packet(int counter, out byte[] ax25Raw, out ushort fcs)
    {
        ax25Raw = BuildAx25Raw(counter);
        fcs = CalculateFcs(ax25Raw);
        byte[] wireFcs = FcsWireBytes(fcs);
        if (!VerifyFcs(ax25Raw, wireFcs))
            throw new InvalidOperationException("AX.25 FCS self-verification failed; RTX1 packet must not be sent.");

        var packet = new byte[Rtx1Prefix.Length + ax25Raw.Length + wireFcs.Length];
        Buffer.BlockCopy(Rtx1Prefix, 0, packet, 0, Rtx1Prefix.Length);
        Buffer.BlockCopy(ax25Raw, 0, packet, Rtx1Prefix.Length, ax25Raw.Length);
        Buffer.BlockCopy(wireFcs, 0, packet, Rtx1Prefix.Length + ax25Raw.Length, wireFcs.Length);
        return packet;
    }

    public static IReadOnlyList<byte[]> SplitChunks(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0)
            return Array.Empty<byte[]>();

        var chunks = new List<byte[]>((packet.Length + ChunkSize - 1) / ChunkSize);
        for (int offset = 0; offset < packet.Length; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, packet.Length - offset);
            chunks.Add(packet.Slice(offset, count).ToArray());
        }
        return chunks;
    }
}
