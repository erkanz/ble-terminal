using System.Text;

namespace BLESerialTerminal;

internal static class Ax25Decoder
{
    private const int AddressBytes = 7;
    private const int MaxAddresses = 10;

    public static bool TryDecode(ReadOnlySpan<byte> frame, out Ax25Packet? packet, out string error)
    {
        packet = null;
        error = string.Empty;

        if (frame.Length < (AddressBytes * 2) + 1)
        {
            error = $"AX.25 frame too short ({frame.Length} bytes)";
            return false;
        }

        var addresses = new List<Ax25Address>();
        var warnings = new List<string>();
        int offset = 0;
        bool foundLast = false;

        for (int addressIndex = 0; addressIndex < MaxAddresses; addressIndex++)
        {
            if (offset + AddressBytes > frame.Length)
            {
                error = "AX.25 address field is truncated";
                return false;
            }

            Ax25Address address = DecodeAddress(frame.Slice(offset, AddressBytes), addressIndex, warnings);
            addresses.Add(address);
            offset += AddressBytes;

            if (address.Last)
            {
                foundLast = true;
                break;
            }
        }

        if (!foundLast)
        {
            error = "AX.25 address extension bit was not found";
            return false;
        }

        if (addresses.Count < 2)
        {
            error = "AX.25 frame does not contain destination and source addresses";
            return false;
        }

        if (offset >= frame.Length)
        {
            error = "AX.25 control field is missing";
            return false;
        }

        byte control = frame[offset++];
        string frameType = DecodeFrameType(control);
        byte? pid = null;

        if (FrameCarriesPid(control))
        {
            if (offset >= frame.Length)
            {
                error = $"AX.25 {frameType} frame is missing PID";
                return false;
            }
            pid = frame[offset++];
        }

        byte[] information = frame[offset..].ToArray();
        var digipeaters = addresses.Skip(2).ToList();

        if (addresses.Count == MaxAddresses && !addresses[^1].Last)
            warnings.Add("AX.25 address list exceeded decoder limit");

        packet = new Ax25Packet(
            addresses[0],
            addresses[1],
            digipeaters,
            control,
            pid,
            information,
            frameType,
            warnings);
        return true;
    }

    private static Ax25Address DecodeAddress(ReadOnlySpan<byte> data, int index, List<string> warnings)
    {
        var callsign = new StringBuilder(6);
        for (int i = 0; i < 6; i++)
        {
            byte raw = data[i];
            char c = (char)(raw >> 1);
            if (c == '\0') c = ' ';
            callsign.Append(c);

            if ((raw & 0x01) != 0)
                warnings.Add($"Address {index + 1} character byte {i + 1} has unexpected low bit set");
        }

        string call = callsign.ToString().TrimEnd();
        if (string.IsNullOrWhiteSpace(call))
        {
            call = "?";
            warnings.Add($"Address {index + 1} has an empty callsign");
        }
        else if (call.Any(c => c < 0x20 || c > 0x7E))
        {
            warnings.Add($"Address {index + 1} contains non-printable callsign characters");
        }

        byte ssidByte = data[6];
        int ssid = (ssidByte >> 1) & 0x0F;
        bool last = (ssidByte & 0x01) != 0;
        bool repeated = index >= 2 && (ssidByte & 0x80) != 0;

        return new Ax25Address(call, ssid, repeated, last);
    }

    private static bool FrameCarriesPid(byte control)
    {
        // I frames carry PID. UI is the APRS U-frame and also carries PID.
        if ((control & 0x01) == 0)
            return true;

        return (control & 0xEF) == 0x03;
    }

    private static string DecodeFrameType(byte control)
    {
        if ((control & 0x01) == 0)
            return "I";

        if ((control & 0x03) == 0x01)
        {
            return (control >> 2) & 0x03 switch
            {
                0 => "RR",
                1 => "RNR",
                2 => "REJ",
                3 => "SREJ",
                _ => "S"
            };
        }

        return control & 0xEF switch
        {
            0x03 => "UI",
            0x2F => "SABM",
            0x6F => "SABME",
            0x43 => "DISC",
            0x0F => "DM",
            0x63 => "UA",
            0x87 => "FRMR",
            0xAF => "XID",
            0xE3 => "TEST",
            _ => "U"
        };
    }
}
