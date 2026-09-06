namespace BLESerialTerminal;

internal static class Program
{
    private static int _passed;

    private static async Task<int> Main()
    {
        try
        {
            TestHexNoneExactWireBytes();
            TestCompactHex();
            TestLineEndings();
            TestInvalidHex();
            TestTextNone();
            TestRt950HandshakeFixture();
            TestRt950AddressEncoding();
            TestRt950FcsDeterministicOutput();
            TestRt950FcsRoundtripVerify();
            TestRt950Rtx1PrefixAndChunkSplit();
            TestRt950CharacteristicRouting();
            await TestSerializedQueueOrderAsync();
            await TestRapidManyCommandQueueAsync();

            Console.WriteLine();
            Console.WriteLine($"MANUAL TX TESTS: PASS ({_passed} checks)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("MANUAL TX TESTS: FAIL");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void TestHexNoneExactWireBytes()
    {
        const string input = "50 52 4F 47 52 41 4D 42 54 39 30 30 30 55";
        byte[] payload = TxPayloadBuilder.Build(input, hexMode: true, TxLineEnding.None);
        byte[] expected = new byte[]
        {
            0x50, 0x52, 0x4F, 0x47, 0x52, 0x41, 0x4D, 0x42, 0x54, 0x39, 0x30, 0x30, 0x30, 0x55
        };
        Check(payload.SequenceEqual(expected), "HEX + NONE preserves exact OEM handshake bytes");
        Check(payload.Length == 14, "HEX + NONE adds no trailing byte");
    }

    private static void TestCompactHex()
    {
        byte[] spaced = TxPayloadBuilder.Build("01 A0 FF", true, TxLineEnding.None);
        byte[] compact = TxPayloadBuilder.Build("01A0FF", true, TxLineEnding.None);
        Check(spaced.SequenceEqual(new byte[] { 0x01, 0xA0, 0xFF }), "spaced HEX accepted");
        Check(compact.SequenceEqual(spaced), "compact HEX accepted");
    }

    private static void TestLineEndings()
    {
        Check(TxPayloadBuilder.Build("41", true, TxLineEnding.Lf).SequenceEqual(new byte[] { 0x41, 0x0A }), "LF appends 0A");
        Check(TxPayloadBuilder.Build("41", true, TxLineEnding.Cr).SequenceEqual(new byte[] { 0x41, 0x0D }), "CR appends 0D");
        Check(TxPayloadBuilder.Build("41", true, TxLineEnding.CrLf).SequenceEqual(new byte[] { 0x41, 0x0D, 0x0A }), "CRLF appends 0D 0A");
        Check(TxPayloadBuilder.Build("41", true, TxLineEnding.None).SequenceEqual(new byte[] { 0x41 }), "NONE appends nothing");
    }

    private static void TestInvalidHex()
    {
        CheckThrows<FormatException>(() => TxPayloadBuilder.Build("ABC", true, TxLineEnding.None), "odd HEX rejected");
        CheckThrows<FormatException>(() => TxPayloadBuilder.Build("GG", true, TxLineEnding.None), "invalid HEX rejected");
    }

    private static void TestTextNone()
    {
        byte[] data = TxPayloadBuilder.Build("PROGRAMBT9000U", false, TxLineEnding.None);
        Check(System.Text.Encoding.UTF8.GetString(data) == "PROGRAMBT9000U", "TEXT + NONE unchanged");
    }

    private static void TestRt950HandshakeFixture()
    {
        byte[] expected =
        {
            0x3F, 0x3F, 0x3F, 0x3F, 0x02, 0x2E, 0x17, 0x1D, 0x5E, 0x57,
            0x25, 0x2F, 0x57, 0x13, 0x62, 0x56, 0x04, 0x4B, 0x23, 0x42
        };
        Check(Rt950Rtx1Protocol.OemHandshake.SequenceEqual(expected), "RTX1 OEM handshake is exact captured 20-byte fixture");
        Check(Rt950Rtx1Protocol.OemHandshake.Length == 20, "RTX1 OEM handshake length is exactly 20 bytes");
    }

    private static void TestRt950AddressEncoding()
    {
        byte[] destination = Rt950Rtx1Protocol.EncodeAddress("APRS", 0, last: false);
        byte[] source = Rt950Rtx1Protocol.EncodeAddress("TEST", 1, last: true);

        Check(destination.SequenceEqual(new byte[] { 0x82, 0xA0, 0xA4, 0xA6, 0x40, 0x40, 0x60 }),
            "APRS destination address encoding is deterministic");
        Check(source.SequenceEqual(new byte[] { 0xA8, 0x8A, 0xA6, 0xA8, 0x40, 0x40, 0x63 }),
            "TEST-1 source address encoding is deterministic");
        Check((destination[6] & 0x01) == 0 && (source[6] & 0x01) == 1,
            "AX.25 extension bit is set only on final source address");
    }

    private static void TestRt950FcsDeterministicOutput()
    {
        byte[] raw = Rt950Rtx1Protocol.BuildAx25Raw(1);
        ushort fcs = Rt950Rtx1Protocol.CalculateFcs(raw);
        byte[] wire = Rt950Rtx1Protocol.FcsWireBytes(fcs);

        Check(raw.Length == 45, "TEST-1/APRS #0001 raw AX.25 length is 45 bytes");
        Check(fcs == 0xC48E, "TEST-1/APRS #0001 AX.25 FCS is deterministic 0xC48E");
        Check(wire.SequenceEqual(new byte[] { 0x8E, 0xC4 }), "AX.25 FCS is serialized little-endian as 8E C4");
    }

    private static void TestRt950FcsRoundtripVerify()
    {
        byte[] raw = Rt950Rtx1Protocol.BuildAx25Raw(1);
        byte[] wire = Rt950Rtx1Protocol.FcsWireBytes(Rt950Rtx1Protocol.CalculateFcs(raw));
        Check(Rt950Rtx1Protocol.VerifyFcs(raw, wire), "AX.25 FCS roundtrip verify passes");

        byte[] damaged = raw.ToArray();
        damaged[^1] ^= 0x01;
        Check(!Rt950Rtx1Protocol.VerifyFcs(damaged, wire), "AX.25 FCS verify rejects modified frame");
    }

    private static void TestRt950Rtx1PrefixAndChunkSplit()
    {
        byte[] packet = Rt950Rtx1Protocol.BuildRtx1Packet(1, out byte[] raw, out ushort fcs);
        IReadOnlyList<byte[]> chunks = Rt950Rtx1Protocol.SplitChunks(packet);

        Check(packet.AsSpan(0, 4).SequenceEqual(new byte[] { 0x52, 0x54, 0x58, 0x31 }), "RTX1 ASCII prefix is exact");
        Check(packet.Length == 51 && raw.Length == 45 && fcs == 0xC48E, "RTX1 #0001 total packet is deterministic 51 bytes");
        Check(chunks.Count == 3, "RTX1 packet splits into three BLE chunks");
        Check(chunks[0].Length == 20 && chunks[1].Length == 20 && chunks[2].Length == 11,
            "RTX1 chunks are 20 + 20 + final short 11 bytes");
        Check(chunks.SelectMany(chunk => chunk).SequenceEqual(packet), "RTX1 chunk concatenation reproduces original packet");
        Check(System.Text.Encoding.ASCII.GetString(raw.AsSpan(16)) == ">RT950 RTX1 BLE TX TEST #0001",
            "RTX1 test information field uses TEST counter #0001");
    }

    private static void TestRt950CharacteristicRouting()
    {
        Check(Rt950Rtx1Protocol.HandshakeCharacteristicUuid == "FF31",
            "routing assertion: handshake characteristic == FF31");

        byte[] packet = Rt950Rtx1Protocol.BuildRtx1Packet(1, out _, out _);
        IReadOnlyList<byte[]> chunks = Rt950Rtx1Protocol.SplitChunks(packet);
        string[] allRtx1ChunkCharacteristics = chunks
            .Select(_ => Rt950Rtx1Protocol.Rtx1CharacteristicUuid)
            .ToArray();
        Check(allRtx1ChunkCharacteristics.All(uuid => uuid == "FFE1"),
            "routing assertion: all RTX1 chunk characteristics == FFE1");
        Check(Rt950Rtx1Protocol.ServiceUuid == "FFE0", "routing assertion: RTX1 service == FFE0");
    }

    private static async Task TestSerializedQueueOrderAsync()
    {
        var queue = new SerialTaskQueue();
        var order = new List<string>();

        Task first = queue.Enqueue(async () =>
        {
            order.Add("start1");
            await Task.Delay(40);
            order.Add("end1");
        });
        Task second = queue.Enqueue(async () =>
        {
            order.Add("start2");
            await Task.Delay(1);
            order.Add("end2");
        });

        await Task.WhenAll(first, second);
        Check(order.SequenceEqual(new[] { "start1", "end1", "start2", "end2" }), "rapid command writes remain serialized");
    }

    private static async Task TestRapidManyCommandQueueAsync()
    {
        const int count = 32;
        var queue = new SerialTaskQueue();
        var sent = new List<string>();
        var tasks = new List<Task>();

        for (int i = 0; i < count; i++)
        {
            int command = i + 1;
            string payload = $"P{command:D2}";
            tasks.Add(queue.Enqueue(async () =>
            {
                await Task.Delay(command % 3);
                sent.Add($"{command}:{payload}");
            }));
        }

        await Task.WhenAll(tasks);
        string[] expected = Enumerable.Range(1, count).Select(i => $"{i}:P{i:D2}").ToArray();
        Check(sent.SequenceEqual(expected), "more than five command sends preserve queue order and payload boundaries");
    }

    private static void CheckThrows<T>(Action action, string name) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            Check(true, name);
            return;
        }
        throw new InvalidOperationException($"FAIL: {name}");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"FAIL: {name}");
        _passed++;
        Console.WriteLine($"PASS  {name}");
    }
}
