using System.Globalization;

namespace BLESerialTerminal;

internal static class Program
{
    private static int _passed;

    private static int Main()
    {
        try
        {
            TestKissCompleteFrame();
            TestKissFragmentedFrame();
            TestKissMultipleFrames();
            TestKissEscapes();
            TestKissMalformedEscape();
            TestAx25WithoutFcs();
            TestAprsPosition();
            TestAprsMessageAck();
            TestAprsObjectAndItem();
            TestAprsUnsupportedType();
            TestRealRt950FrameFromPhaseA();

            Console.WriteLine();
            Console.WriteLine($"PROTOCOL TESTS: PASS ({_passed} checks)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("PROTOCOL TESTS: FAIL");
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void TestKissCompleteFrame()
    {
        var decoder = new KissStreamDecoder();
        List<KissFrame> frames = decoder.Push(new byte[] { 0xC0, 0x00, 0x11, 0x22, 0xC0 }).ToList();
        Check(frames.Count == 1, "KISS complete frame count");
        Check(frames[0].IsDataFrame, "KISS data command recognized");
        Check(frames[0].Port == 0 && frames[0].Command == 0, "KISS port/command split");
        Check(frames[0].Data.SequenceEqual(new byte[] { 0x11, 0x22 }), "KISS data payload extracted");
    }

    private static void TestKissFragmentedFrame()
    {
        var decoder = new KissStreamDecoder();
        Check(!decoder.Push(new byte[] { 0xC0, 0x00, 0xAA }).Any(), "KISS fragment does not emit early frame");
        Check(!decoder.Push(new byte[] { 0xBB, 0xCC }).Any(), "KISS middle fragment does not emit early frame");
        List<KissFrame> frames = decoder.Push(new byte[] { 0xDD, 0xC0 }).ToList();
        Check(frames.Count == 1, "KISS fragmented frame completes on trailing FEND");
        Check(frames[0].Data.SequenceEqual(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }), "KISS fragmented bytes preserved");
    }

    private static void TestKissMultipleFrames()
    {
        var decoder = new KissStreamDecoder();
        List<KissFrame> frames = decoder.Push(new byte[]
        {
            0xC0, 0x00, 0x01, 0xC0,
            0x00, 0x02, 0xC0
        }).ToList();
        Check(frames.Count == 2, "KISS two frames in one notification");
        Check(frames[0].Data.SequenceEqual(new byte[] { 0x01 }) && frames[1].Data.SequenceEqual(new byte[] { 0x02 }), "KISS multi-frame payloads");
    }

    private static void TestKissEscapes()
    {
        var decoder = new KissStreamDecoder();
        KissFrame frame = decoder.Push(new byte[] { 0xC0, 0x00, 0xDB, 0xDC, 0xDB, 0xDD, 0xC0 }).Single();
        Check(frame.Data.SequenceEqual(new byte[] { 0xC0, 0xDB }), "KISS FEND/FESC unescape");
        Check(!frame.IsMalformed, "Valid KISS escapes have no warning");
    }

    private static void TestKissMalformedEscape()
    {
        var decoder = new KissStreamDecoder();
        KissFrame frame = decoder.Push(new byte[] { 0xC0, 0x00, 0xDB, 0xAA, 0xC0 }).Single();
        Check(frame.IsMalformed, "Malformed KISS escape is flagged");
        Check(frame.Data.SequenceEqual(new byte[] { 0xDB, 0xAA }), "Malformed KISS escape bytes remain visible");
    }

    private static void TestAx25WithoutFcs()
    {
        byte[] ax25 = BuildUiFrame("APRS", 0, "N0CALL", 1, Array.Empty<(string, int, bool)>(), ">hello");
        Check(Ax25Decoder.TryDecode(ax25, out Ax25Packet? packet, out string error), $"AX.25 UI decode without FCS ({error})");
        Check(packet != null && packet.Source.Display == "N0CALL-1", "AX.25 source callsign/SSID");
        Check(packet != null && packet.Destination.Display == "APRS", "AX.25 destination callsign");
        Check(packet != null && packet.FrameType == "UI" && packet.Pid == 0xF0, "AX.25 UI + PID F0");
        Check(packet != null && packet.FcsStatus.Contains("KISS", StringComparison.OrdinalIgnoreCase), "AX.25 missing FCS is documented, not rejected");
    }

    private static void TestAprsPosition()
    {
        byte[] ax25 = BuildUiFrame("APRS", 0, "N0CALL", 1, new[] { ("WIDE1", 1, false) }, "!4903.50N/07201.75W-Test/A=001234");
        Check(Ax25Decoder.TryDecode(ax25, out Ax25Packet? packet, out string error), $"AX.25 position fixture ({error})");
        AprsPacket? aprs = AprsDecoder.Decode(packet!);
        Check(aprs != null && aprs.Category.StartsWith("Position", StringComparison.Ordinal), "APRS position category");
        Check(aprs != null && aprs.Fields.ContainsKey("Latitude") && aprs.Fields.ContainsKey("Longitude"), "APRS position coordinates parsed");
        double lat = double.Parse(aprs!.Fields["Latitude"], CultureInfo.InvariantCulture);
        double lon = double.Parse(aprs.Fields["Longitude"], CultureInfo.InvariantCulture);
        Check(Math.Abs(lat - 49.058333) < 0.00001 && Math.Abs(lon - (-72.029167)) < 0.00001, "APRS uncompressed coordinate math");
        Check(aprs.Fields.TryGetValue("Altitude feet", out string? altitude) && altitude == "001234", "APRS altitude extension");
    }

    private static void TestAprsMessageAck()
    {
        string info = ":" + "N0CALL-1".PadRight(9) + ":ack123";
        byte[] ax25 = BuildUiFrame("APRS", 0, "TEST", 0, Array.Empty<(string, int, bool)>(), info);
        Check(Ax25Decoder.TryDecode(ax25, out Ax25Packet? packet, out string error), $"AX.25 message fixture ({error})");
        AprsPacket? aprs = AprsDecoder.Decode(packet!);
        Check(aprs?.Category == "Message ACK", "APRS ACK category");
        Check(aprs != null && aprs.Fields.TryGetValue("ACK ID", out string? ack) && ack == "123", "APRS ACK id parsed");
    }

    private static void TestAprsObjectAndItem()
    {
        string obj = ";" + "TESTOBJ".PadRight(9) + "*111111z4903.50N/07201.75W>Object";
        byte[] objectFrame = BuildUiFrame("APRS", 0, "TEST", 0, Array.Empty<(string, int, bool)>(), obj);
        Check(Ax25Decoder.TryDecode(objectFrame, out Ax25Packet? objectAx25, out _), "AX.25 object fixture");
        AprsPacket? objectAprs = AprsDecoder.Decode(objectAx25!);
        Check(objectAprs?.Category == "Object", "APRS object category");
        Check(objectAprs != null && objectAprs.Fields.TryGetValue("State", out string? objectState) && objectState == "Alive", "APRS object alive state");

        string item = ")ITEM!4903.50N/07201.75W>Item";
        byte[] itemFrame = BuildUiFrame("APRS", 0, "TEST", 0, Array.Empty<(string, int, bool)>(), item);
        Check(Ax25Decoder.TryDecode(itemFrame, out Ax25Packet? itemAx25, out _), "AX.25 item fixture");
        AprsPacket? itemAprs = AprsDecoder.Decode(itemAx25!);
        Check(itemAprs?.Category == "Item", "APRS item category");
        Check(itemAprs != null && itemAprs.Fields.TryGetValue("Item name", out string? itemName) && itemName == "ITEM", "APRS item name");
    }

    private static void TestAprsUnsupportedType()
    {
        byte[] ax25 = BuildUiFrame("APRS", 0, "TEST", 0, Array.Empty<(string, int, bool)>(), "$vendor payload");
        Check(Ax25Decoder.TryDecode(ax25, out Ax25Packet? packet, out _), "AX.25 unsupported APRS fixture");
        AprsPacket? aprs = AprsDecoder.Decode(packet!);
        Check(aprs?.Category == "Unknown / unsupported", "Unsupported APRS type remains visible");
        Check(aprs?.RawInformation == "$vendor payload", "Unsupported APRS raw information preserved");
    }

    private static void TestRealRt950FrameFromPhaseA()
    {
        byte[] kissPayload = HexBytes(
            "00 82 A0 82 A8 70 62 E2 96 8A 64 84 A6 88 6E AE 92 88 8A 62 40 62 AE 92 88 8A 64 40 63 03 F0 21 33 34 31 32 2E 37 33 4E 2F 31 30 38 34 39 2E 3A 30 45 26 33 34 38 2F 30 30 31 2F 41 3D 30 30 30 30 30 30 41 50 52 53 43 4E 20 57 49 46 49 20 34 2E 33 30 56");
        Check(kissPayload[0] == 0x00, "RT-950 fixture KISS command byte");
        Check(Ax25Decoder.TryDecode(kissPayload.AsSpan(1), out Ax25Packet? packet, out string error), $"Real RT-950 Phase A AX.25 frame decodes ({error})");
        Check(packet != null && packet.FrameType == "UI" && packet.Pid == 0xF0, "Real RT-950 frame is AX.25 UI/PID F0");
        AprsPacket? aprs = AprsDecoder.Decode(packet!);
        Check(aprs != null && aprs.RawInformation.StartsWith("!3412.73N/", StringComparison.Ordinal), "Real RT-950 APRS information preserved");
    }

    private static byte[] BuildUiFrame(
        string destination,
        int destinationSsid,
        string source,
        int sourceSsid,
        IReadOnlyList<(string Call, int Ssid, bool Repeated)> path,
        string info)
    {
        var bytes = new List<byte>();
        bytes.AddRange(EncodeAddress(destination, destinationSsid, last: false, repeated: false));
        bool sourceLast = path.Count == 0;
        bytes.AddRange(EncodeAddress(source, sourceSsid, last: sourceLast, repeated: false));
        for (int i = 0; i < path.Count; i++)
        {
            (string call, int ssid, bool repeated) = path[i];
            bytes.AddRange(EncodeAddress(call, ssid, last: i == path.Count - 1, repeated));
        }
        bytes.Add(0x03);
        bytes.Add(0xF0);
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(info));
        return bytes.ToArray();
    }

    private static byte[] EncodeAddress(string callsign, int ssid, bool last, bool repeated)
    {
        string call = callsign.ToUpperInvariant().PadRight(6).Substring(0, 6);
        byte[] result = new byte[7];
        for (int i = 0; i < 6; i++)
            result[i] = (byte)(call[i] << 1);

        result[6] = (byte)(0x60 | ((ssid & 0x0F) << 1) | (last ? 0x01 : 0x00));
        if (repeated) result[6] |= 0x80;
        return result;
    }

    private static byte[] HexBytes(string hex) =>
        hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
           .Select(x => byte.Parse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
           .ToArray();

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"FAIL: {name}");
        _passed++;
        Console.WriteLine($"PASS  {name}");
    }
}
