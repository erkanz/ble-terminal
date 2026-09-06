using System.Globalization;
using System.Text.RegularExpressions;

namespace BLESerialTerminal;

internal static class AprsDecoder
{
    private static readonly Regex WeatherTokenRegex = new(
        @"(?:(?:c|s|g|t|r|p|P)\d{3}|h\d{2}|b\d{5})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AprsPacket? Decode(Ax25Packet ax25)
    {
        if (!ax25.IsAprsUiFrame)
            return null;

        string info = ax25.InformationText;
        if (info.Length == 0)
            return new AprsPacket("Empty APRS", '\0', info, "Empty APRS information field");

        char type = info[0];
        return type switch
        {
            '!' or '=' => DecodePosition(info, timestamped: false),
            '/' or '@' => DecodePosition(info, timestamped: true),
            ':' => DecodeMessage(info),
            '>' => Simple("Status", info, info.Length > 1 ? info[1..] : string.Empty),
            ';' => DecodeObject(info),
            ')' => DecodeItem(info),
            '_' => DecodeWeather(info),
            '`' or '\'' => new AprsPacket("Mic-E", type, info, "Mic-E position/status packet (raw payload preserved)"),
            '}' => DecodeThirdParty(info),
            '?' => Simple("Query", info, info.Length > 1 ? info[1..] : string.Empty),
            '<' => Simple("Station capabilities", info, info.Length > 1 ? info[1..] : string.Empty),
            '{' => Simple("User defined", info, info.Length > 1 ? info[1..] : string.Empty),
            'T' when info.StartsWith("T#", StringComparison.Ordinal) => DecodeTelemetry(info),
            _ => new AprsPacket(
                "Unknown / unsupported",
                type,
                info,
                $"Unsupported APRS data type 0x{(byte)type:X2} ('{Printable(type)}')",
                warnings: new[] { "Raw APRS information is preserved for analysis" })
        };
    }

    private static AprsPacket DecodePosition(string info, bool timestamped)
    {
        var fields = new Dictionary<string, string>();
        var warnings = new List<string>();
        int offset = 1;

        if (timestamped)
        {
            if (info.Length < 8)
            {
                warnings.Add("Timestamped position is too short to contain the 7-character APRS timestamp");
                return new AprsPacket("Timestamped position", info[0], info, "Timestamped position (truncated)", fields, warnings);
            }

            fields["Timestamp"] = info.Substring(1, 7);
            offset = 8;
        }

        string body = offset < info.Length ? info[offset..] : string.Empty;
        string category = timestamped ? "Timestamped position" : "Position";
        string summary = category;

        if (TryParseUncompressedPosition(body, out PositionResult? position, out string positionWarning))
        {
            fields["Latitude"] = position.Latitude.ToString("F6", CultureInfo.InvariantCulture);
            fields["Longitude"] = position.Longitude.ToString("F6", CultureInfo.InvariantCulture);
            fields["Symbol table"] = position.SymbolTable.ToString();
            fields["Symbol"] = position.SymbolCode.ToString();
            summary = $"{category} {position.Latitude:F5}, {position.Longitude:F5}";

            string comment = body.Length > position.Consumed ? body[position.Consumed..] : string.Empty;
            if (comment.Length > 0)
            {
                fields["Comment/raw extension"] = comment;
                DecodeCourseSpeedAltitudeAndWeather(comment, fields);
                if (ContainsWeather(fields))
                    category += " / Weather";
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(positionWarning))
                warnings.Add(positionWarning);
            fields["Raw position body"] = body;
            summary = $"{category} (coordinates not decoded)";
        }

        return new AprsPacket(category, info[0], info, summary, fields, warnings);
    }

    private static AprsPacket DecodeMessage(string info)
    {
        var fields = new Dictionary<string, string>();
        var warnings = new List<string>();

        if (info.Length < 11 || info[10] != ':')
        {
            warnings.Add("APRS message does not contain the fixed 9-character addressee field");
            return new AprsPacket("Message", ':', info, "Malformed APRS message", fields, warnings);
        }

        string addressee = info.Substring(1, 9).Trim();
        string body = info.Length > 11 ? info[11..] : string.Empty;
        fields["Addressee"] = addressee;

        int messageIdIndex = body.LastIndexOf('{');
        if (messageIdIndex >= 0 && messageIdIndex + 1 < body.Length)
        {
            fields["Message ID"] = body[(messageIdIndex + 1)..];
            body = body[..messageIdIndex];
        }
        fields["Message"] = body;

        if (body.StartsWith("ack", StringComparison.OrdinalIgnoreCase))
        {
            fields["ACK ID"] = body.Length > 3 ? body[3..] : string.Empty;
            return new AprsPacket("Message ACK", ':', info, $"ACK from/to {addressee}: {body}", fields, warnings);
        }

        if (body.StartsWith("rej", StringComparison.OrdinalIgnoreCase))
        {
            fields["REJ ID"] = body.Length > 3 ? body[3..] : string.Empty;
            return new AprsPacket("Message REJ", ':', info, $"REJ from/to {addressee}: {body}", fields, warnings);
        }

        return new AprsPacket("Message", ':', info, $"Message to {addressee}: {body}", fields, warnings);
    }

    private static AprsPacket DecodeObject(string info)
    {
        var fields = new Dictionary<string, string>();
        var warnings = new List<string>();
        if (info.Length < 11)
        {
            warnings.Add("Object packet is too short");
            return new AprsPacket("Object", ';', info, "Object (truncated)", fields, warnings);
        }

        string name = info.Substring(1, Math.Min(9, info.Length - 1)).TrimEnd();
        fields["Object name"] = name;
        char state = info.Length > 10 ? info[10] : '?';
        fields["State"] = state == '*' ? "Alive" : state == '_' ? "Killed" : $"Unknown ({state})";

        int positionOffset = 11;
        if (info.Length >= 18)
        {
            fields["Timestamp"] = info.Substring(11, 7);
            positionOffset = 18;
        }
        else
        {
            warnings.Add("Object timestamp is truncated");
        }

        if (positionOffset < info.Length)
        {
            string body = info[positionOffset..];
            if (TryParseUncompressedPosition(body, out PositionResult? position, out string warning))
            {
                fields["Latitude"] = position.Latitude.ToString("F6", CultureInfo.InvariantCulture);
                fields["Longitude"] = position.Longitude.ToString("F6", CultureInfo.InvariantCulture);
                if (body.Length > position.Consumed)
                    fields["Comment/raw extension"] = body[position.Consumed..];
            }
            else if (!string.IsNullOrWhiteSpace(warning))
            {
                warnings.Add(warning);
            }
        }

        return new AprsPacket("Object", ';', info, $"Object {name} ({fields["State"]})", fields, warnings);
    }

    private static AprsPacket DecodeItem(string info)
    {
        var fields = new Dictionary<string, string>();
        var warnings = new List<string>();
        int delimiter = -1;
        for (int i = 1; i < info.Length; i++)
        {
            if (info[i] == '!' || info[i] == '_')
            {
                delimiter = i;
                break;
            }
        }

        if (delimiter < 0)
        {
            warnings.Add("Item packet has no alive/killed delimiter");
            return new AprsPacket("Item", ')', info, "Malformed APRS item", fields, warnings);
        }

        string name = info.Substring(1, delimiter - 1);
        bool alive = info[delimiter] == '!';
        fields["Item name"] = name;
        fields["State"] = alive ? "Alive" : "Killed";

        string body = delimiter + 1 < info.Length ? info[(delimiter + 1)..] : string.Empty;
        if (TryParseUncompressedPosition(body, out PositionResult? position, out string warning))
        {
            fields["Latitude"] = position.Latitude.ToString("F6", CultureInfo.InvariantCulture);
            fields["Longitude"] = position.Longitude.ToString("F6", CultureInfo.InvariantCulture);
            if (body.Length > position.Consumed)
                fields["Comment/raw extension"] = body[position.Consumed..];
        }
        else if (!string.IsNullOrWhiteSpace(warning))
        {
            warnings.Add(warning);
        }

        return new AprsPacket("Item", ')', info, $"Item {name} ({fields["State"]})", fields, warnings);
    }

    private static AprsPacket DecodeTelemetry(string info)
    {
        var fields = new Dictionary<string, string>();
        var warnings = new List<string>();
        string[] parts = info[2..].Split(',');
        if (parts.Length > 0) fields["Sequence"] = parts[0];
        for (int i = 1; i < parts.Length && i <= 5; i++)
            fields[$"Analog {i}"] = parts[i];
        if (parts.Length > 6) fields["Digital bits"] = parts[6];
        if (parts.Length < 6) warnings.Add("Telemetry packet contains fewer than five analog channels");
        return new AprsPacket("Telemetry", 'T', info, $"Telemetry sequence {(parts.Length > 0 ? parts[0] : "?")}", fields, warnings);
    }

    private static AprsPacket DecodeWeather(string info)
    {
        var fields = new Dictionary<string, string>();
        DecodeWeatherTokens(info.Length > 1 ? info[1..] : string.Empty, fields);
        return new AprsPacket("Weather", '_', info, "APRS weather report", fields);
    }

    private static AprsPacket DecodeThirdParty(string info)
    {
        var fields = new Dictionary<string, string>
        {
            ["Encapsulated packet"] = info.Length > 1 ? info[1..] : string.Empty
        };
        return new AprsPacket("Third-party", '}', info, "Third-party APRS encapsulation", fields);
    }

    private static AprsPacket Simple(string category, string info, string value)
    {
        var fields = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(value)) fields["Text"] = value;
        return new AprsPacket(category, info[0], info, string.IsNullOrEmpty(value) ? category : $"{category}: {value}", fields);
    }

    private static bool TryParseUncompressedPosition(string body, out PositionResult? result, out string warning)
    {
        result = null;
        warning = string.Empty;
        if (body.Length < 19)
        {
            warning = $"Uncompressed APRS position requires 19 characters; only {body.Length} available";
            return false;
        }

        string latText = body.Substring(0, 8);
        char symbolTable = body[8];
        string lonText = body.Substring(9, 9);
        char symbolCode = body[18];

        if (latText[4] != '.' || (latText[7] != 'N' && latText[7] != 'S') ||
            lonText[5] != '.' || (lonText[8] != 'E' && lonText[8] != 'W'))
        {
            warning = $"Position is not standard uncompressed DDMM.mmN/DDDMM.mmE format: '{body[..19]}'";
            return false;
        }

        if (!int.TryParse(latText.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int latDeg) ||
            !double.TryParse(latText.AsSpan(2, 5), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double latMin) ||
            !int.TryParse(lonText.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int lonDeg) ||
            !double.TryParse(lonText.AsSpan(3, 5), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double lonMin))
        {
            warning = $"Position contains non-numeric latitude/longitude digits: '{body[..19]}'";
            return false;
        }

        if (latDeg > 90 || lonDeg > 180 || latMin >= 60 || lonMin >= 60)
        {
            warning = $"Position is outside valid latitude/longitude ranges: '{body[..19]}'";
            return false;
        }

        double latitude = latDeg + latMin / 60.0;
        double longitude = lonDeg + lonMin / 60.0;
        if (latText[7] == 'S') latitude = -latitude;
        if (lonText[8] == 'W') longitude = -longitude;

        result = new PositionResult(latitude, longitude, symbolTable, symbolCode, 19);
        return true;
    }

    private static void DecodeCourseSpeedAltitudeAndWeather(string extension, Dictionary<string, string> fields)
    {
        if (extension.Length >= 7 &&
            int.TryParse(extension.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int course) &&
            extension[3] == '/' &&
            int.TryParse(extension.AsSpan(4, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int speed))
        {
            fields["Course degrees"] = course.ToString(CultureInfo.InvariantCulture);
            fields["Speed knots"] = speed.ToString(CultureInfo.InvariantCulture);
        }

        int altitudeIndex = extension.IndexOf("/A=", StringComparison.Ordinal);
        if (altitudeIndex >= 0 && altitudeIndex + 9 <= extension.Length)
        {
            string altitude = extension.Substring(altitudeIndex + 3, 6);
            if (altitude.All(char.IsDigit))
                fields["Altitude feet"] = altitude;
        }

        DecodeWeatherTokens(extension, fields);
    }

    private static void DecodeWeatherTokens(string text, Dictionary<string, string> fields)
    {
        int index = 1;
        foreach (Match match in WeatherTokenRegex.Matches(text))
            fields[$"Weather token {index++}"] = match.Value;
    }

    private static bool ContainsWeather(Dictionary<string, string> fields) =>
        fields.Keys.Any(k => k.StartsWith("Weather token ", StringComparison.Ordinal));

    private static char Printable(char c) => c is >= ' ' and <= '~' ? c : '?';

    private sealed record PositionResult(double Latitude, double Longitude, char SymbolTable, char SymbolCode, int Consumed);
}
