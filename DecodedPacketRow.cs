using System.Text;

namespace BLESerialTerminal;

internal sealed class DecodedPacketRow
{
    private DecodedPacketRow(
        DateTime timestamp,
        string characteristic,
        KissFrame kiss,
        Ax25Packet? ax25,
        AprsPacket? aprs,
        string decodeError)
    {
        Timestamp = timestamp;
        Characteristic = characteristic;
        Kiss = kiss;
        Ax25 = ax25;
        Aprs = aprs;
        DecodeError = decodeError;
    }

    public DateTime Timestamp { get; }
    public string Characteristic { get; }
    public KissFrame Kiss { get; }
    public Ax25Packet? Ax25 { get; }
    public AprsPacket? Aprs { get; }
    public string DecodeError { get; }

    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
    public string KissText => Kiss.PortCommand.HasValue
        ? $"P{Kiss.Port ?? 0}/C{Kiss.Command ?? 0}"
        : "--";
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

    public static DecodedPacketRow Decode(DateTime timestamp, string characteristic, KissFrame kiss)
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
            // Raw KISS data remains available in DetailText even when AX.25 parsing fails.
        }
        else
        {
            aprs = AprsDecoder.Decode(ax25!);
        }

        return new DecodedPacketRow(timestamp, characteristic, kiss, ax25, aprs, error);
    }

    public string DetailText
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Time: {Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"BLE characteristic: {Characteristic}");
            sb.AppendLine();
            sb.AppendLine("KISS");
            sb.AppendLine($"  Port/command byte: {(Kiss.PortCommand.HasValue ? $"0x{Kiss.PortCommand.Value:X2}" : "--")}");
            sb.AppendLine($"  Port: {Kiss.Port?.ToString() ?? "--"}");
            sb.AppendLine($"  Command: {Kiss.Command?.ToString() ?? "--"}");
            sb.AppendLine($"  Data frame: {(Kiss.IsDataFrame ? "YES" : "NO")}");
            sb.AppendLine($"  Raw length: {Kiss.Raw.Length}");
            sb.AppendLine($"  Unescaped length: {Kiss.Payload.Length}");
            sb.AppendLine($"  RAW HEX: {Hex(Kiss.Raw)}");
            sb.AppendLine($"  UNESCAPED: {Hex(Kiss.Payload)}");
            if (Kiss.Warnings.Count > 0)
            {
                sb.AppendLine("  KISS warnings:");
                foreach (string warning in Kiss.Warnings)
                    sb.AppendLine($"    - {warning}");
            }

            sb.AppendLine();
            sb.AppendLine("AX.25");
            if (Ax25 == null)
            {
                sb.AppendLine($"  Decode: FAILED / NOT APPLICABLE");
                if (!string.IsNullOrWhiteSpace(DecodeError))
                    sb.AppendLine($"  Reason: {DecodeError}");
            }
            else
            {
                sb.AppendLine($"  Source: {Ax25.Source.Display}");
                sb.AppendLine($"  Destination: {Ax25.Destination.Display}");
                sb.AppendLine($"  Path: {(string.IsNullOrWhiteSpace(Ax25.Path) ? "(none)" : Ax25.Path)}");
                sb.AppendLine($"  Frame type: {Ax25.FrameType}");
                sb.AppendLine($"  Control: 0x{Ax25.Control:X2}");
                sb.AppendLine($"  PID: {(Ax25.Pid.HasValue ? $"0x{Ax25.Pid.Value:X2}" : "--")}");
                sb.AppendLine($"  FCS: {Ax25.FcsStatus}");
                sb.AppendLine($"  Information bytes: {Ax25.Information.Length}");
                sb.AppendLine($"  Information text: {SafeText(Ax25.InformationText)}");
                if (Ax25.Warnings.Count > 0)
                {
                    sb.AppendLine("  AX.25 warnings:");
                    foreach (string warning in Ax25.Warnings)
                        sb.AppendLine($"    - {warning}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("APRS");
            if (Aprs == null)
            {
                sb.AppendLine(Ax25?.IsAprsUiFrame == true
                    ? "  APRS decoder returned no result"
                    : "  Not an APRS UI/PID F0 frame");
            }
            else
            {
                sb.AppendLine($"  Category: {Aprs.Category}");
                sb.AppendLine($"  Type identifier: {(Aprs.TypeIdentifier == '\0' ? "(none)" : Aprs.TypeIdentifier.ToString())}");
                sb.AppendLine($"  Summary: {Aprs.Summary}");
                sb.AppendLine($"  Raw information: {SafeText(Aprs.RawInformation)}");
                foreach (KeyValuePair<string, string> field in Aprs.Fields)
                    sb.AppendLine($"  {field.Key}: {field.Value}");
                if (Aprs.Warnings.Count > 0)
                {
                    sb.AppendLine("  APRS warnings:");
                    foreach (string warning in Aprs.Warnings)
                        sb.AppendLine($"    - {warning}");
                }
            }

            return sb.ToString();
        }
    }

    public string ExportLine =>
        $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff}\t{Characteristic}\t{KissText}\t{Source}\t{Destination}\t{Path}\t{Type}\t{Summary}";

    private static string Hex(byte[] data) => BitConverter.ToString(data).Replace('-', ' ');

    private static string SafeText(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c == '\r') sb.Append("\\r");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\t') sb.Append("\\t");
            else if (char.IsControl(c)) sb.Append($"\\x{(int)c:X2}");
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
