using System.Globalization;

namespace BLESerialTerminal;

internal static class BleUuid
{
    private const string BluetoothBaseSuffix = "00001000800000805f9b34fb";

    public static string Full(Guid uuid) => uuid.ToString("D").ToUpperInvariant();

    public static string Short(Guid uuid)
    {
        string n = uuid.ToString("N");
        if (n.Length == 32 && n.EndsWith(BluetoothBaseSuffix, StringComparison.OrdinalIgnoreCase))
        {
            string first32 = n[..8];
            if (first32.StartsWith("0000", StringComparison.OrdinalIgnoreCase))
                return first32[4..].ToUpperInvariant();
            return first32.ToUpperInvariant();
        }
        return Full(uuid);
    }


    public static bool TryParse(string value, out Guid uuid)
    {
        uuid = Guid.Empty;
        if (Guid.TryParse(value?.Trim(), out uuid))
            return true;

        string compact = (value ?? string.Empty).Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (compact.Length == 4 && ushort.TryParse(compact, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return Guid.TryParse($"0000{compact}-0000-1000-8000-00805F9B34FB", out uuid);
        if (compact.Length == 8 && uint.TryParse(compact, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return Guid.TryParse($"{compact}-0000-1000-8000-00805F9B34FB", out uuid);
        return false;
    }

    public static bool Is(Guid uuid, string shortOrFull)
    {
        if (Guid.TryParse(shortOrFull, out Guid parsed))
            return uuid == parsed;

        string compact = shortOrFull.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (compact.Length == 4 && ushort.TryParse(compact, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return Short(uuid).Equals(compact, StringComparison.OrdinalIgnoreCase);
        if (compact.Length == 8 && uint.TryParse(compact, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return Short(uuid).Equals(compact, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public static string Label(Guid uuid)
    {
        string shortUuid = Short(uuid);
        return shortUuid.ToUpperInvariant() switch
        {
            "FF31" => "FF31 - Control/Auth",
            "FF32" => "FF32 - Preferred Data",
            "FFE1" => "FFE1 - Fallback Data/UART",
            "FFE0" => "FFE0 - BLE UART Service",
            "2902" => "2902 - CCCD",
            _ => shortUuid
        };
    }

    public static string Display(Guid uuid)
    {
        string shortUuid = Short(uuid);
        string full = Full(uuid);
        return shortUuid.Equals(full, StringComparison.OrdinalIgnoreCase)
            ? full
            : $"{Label(uuid)}   [{full}]";
    }
}
