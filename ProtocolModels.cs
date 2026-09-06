using System.Text;

namespace BLESerialTerminal;

internal sealed record Ax25Address(string Callsign, int Ssid, bool Repeated, bool Last)
{
    public string Display => Ssid == 0 ? Callsign : $"{Callsign}-{Ssid}";
    public string PathDisplay => Repeated ? $"{Display}*" : Display;
}

internal sealed class Ax25Packet
{
    public Ax25Packet(
        Ax25Address destination,
        Ax25Address source,
        IReadOnlyList<Ax25Address> digipeaters,
        byte control,
        byte? pid,
        byte[] information,
        string frameType,
        IReadOnlyList<string> warnings)
    {
        Destination = destination;
        Source = source;
        Digipeaters = digipeaters;
        Control = control;
        Pid = pid;
        Information = information;
        FrameType = frameType;
        Warnings = warnings;
    }

    public Ax25Address Destination { get; }
    public Ax25Address Source { get; }
    public IReadOnlyList<Ax25Address> Digipeaters { get; }
    public byte Control { get; }
    public byte? Pid { get; }
    public byte[] Information { get; }
    public string FrameType { get; }
    public IReadOnlyList<string> Warnings { get; }

    public string Path => Digipeaters.Count == 0
        ? string.Empty
        : string.Join(",", Digipeaters.Select(d => d.PathDisplay));

    public string InformationText => Encoding.Latin1.GetString(Information);

    public bool IsAprsUiFrame =>
        FrameType == "UI" && Pid == 0xF0;

    // Most KISS TNCs deliver AX.25 payload bytes without the HDLC FCS.
    // Absence of an FCS here is normal and must not invalidate a packet.
    public string FcsStatus => "Not supplied by KISS transport";
}

internal sealed class AprsPacket
{
    public AprsPacket(
        string category,
        char typeIdentifier,
        string rawInformation,
        string summary,
        IReadOnlyDictionary<string, string>? fields = null,
        IReadOnlyList<string>? warnings = null)
    {
        Category = category;
        TypeIdentifier = typeIdentifier;
        RawInformation = rawInformation;
        Summary = summary;
        Fields = fields ?? new Dictionary<string, string>();
        Warnings = warnings ?? Array.Empty<string>();
    }

    public string Category { get; }
    public char TypeIdentifier { get; }
    public string RawInformation { get; }
    public string Summary { get; }
    public IReadOnlyDictionary<string, string> Fields { get; }
    public IReadOnlyList<string> Warnings { get; }
}
