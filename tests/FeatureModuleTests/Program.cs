using System.Text;

namespace BLESerialTerminal;

internal static class Program
{
    private static int _passed;

    private static async Task<int> Main()
    {
        try
        {
            TestDefaultFeatureStateIsGeneric();
            TestRt950UnlockFixture();
            TestRt950UnlockResponseClassification();
            TestRt950OemAndModelFixtures();
            TestKissFrameBuilder();
            TestKissEscaping();
            TestKissProcessingPolicyGate();
            await TestUnboundedQueueOrderingAsync();

            Console.WriteLine();
            Console.WriteLine($"FEATURE MODULE TESTS: PASS ({_passed} checks)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("FEATURE MODULE TESTS: FAIL");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            KissStreamDecoder.ProcessingPolicy = null;
        }
    }

    private static void TestDefaultFeatureStateIsGeneric()
    {
        var settings = new OptionalFeatureSettings();
        Check(!settings.Rt950ToolsEnabled, "RT950 Tools default OFF");
        Check(!settings.Rt950AutoUnlockOnConnect, "RT950 Auto Unlock default OFF");
        Check(!settings.KissToolsEnabled, "KISS Tools default OFF");
        Check(settings.KissRxDecoderEnabled, "KISS decoder preference can be ready while module remains OFF");
    }

    private static void TestRt950UnlockFixture()
    {
        byte[] expected =
        {
            0x3F, 0x3F, 0x3F, 0x3F, 0x02, 0x2E, 0x17, 0x1D, 0x5E, 0x57,
            0x25, 0x2F, 0x57, 0x13, 0x62, 0x56, 0x04, 0x4B, 0x23, 0x42
        };
        Check(Rt950Protocol.UnlockFrame.Length == 20, "RT950 unlock frame is exactly 20 bytes");
        Check(Rt950Protocol.UnlockFrame.SequenceEqual(expected), "RT950 unlock frame matches verified FF31 fixture");
        Check(Rt950Protocol.IsUnlockFrame(expected), "RT950 unlock frame classifier accepts exact fixture");
        Check(!Rt950Protocol.IsUnlockFrame(Rt950Protocol.OemHandshake), "OEM handshake is not classified as unlock frame");
    }

    private static void TestRt950UnlockResponseClassification()
    {
        byte[] response =
        {
            0x21, 0x21, 0x21, 0x21, 0x21, 0x20, 0x1B, 0x28, 0x06, 0x4E,
            0x32, 0x13, 0x27, 0x33, 0x16, 0x19, 0x3F, 0x14, 0x16, 0x32
        };
        Check(Rt950Protocol.IsUnlockResponse(response), "verified 0x21 RT950 unlock response is recognized");
        Check(!Rt950Protocol.IsUnlockResponse(new byte[] { 0x06 }), "OEM ACK 06 is not unlock response");
        Check(!Rt950Protocol.IsUnlockResponse(new byte[20]), "non-0x21 notification is not unlock response");
    }

    private static void TestRt950OemAndModelFixtures()
    {
        Check(Encoding.ASCII.GetString(Rt950Protocol.OemHandshake) == "PROGRAMBT9000U", "OEM handshake fixture is PROGRAMBT9000U");
        Check(Rt950Protocol.OemHandshake.Length == 14, "OEM handshake is 14 bytes");
        Check(Rt950Protocol.ModelQuery.SequenceEqual(new byte[] { 0x4D }), "model query fixture is 4D");
        Check(Rt950Protocol.IsOemAck(new byte[] { 0x06 }), "single 06 is OEM ACK");
        Check(!Rt950Protocol.IsOemAck(new byte[] { 0x21, 0x06 }), "unlock-style traffic is not OEM ACK");
    }

    private static void TestKissFrameBuilder()
    {
        byte[] frame = KissFrameBuilder.BuildDataFrame(new byte[] { 0x01, 0x02, 0x03 });
        Check(frame.SequenceEqual(new byte[] { 0xC0, 0x00, 0x01, 0x02, 0x03, 0xC0 }), "KISS builder creates C0 00 AX25 C0 data frame");

        var decoder = new KissStreamDecoder();
        KissFrame parsed = decoder.Push(frame).Single();
        Check(parsed.IsDataFrame && parsed.Port == 0 && parsed.Command == 0, "built KISS frame decodes as port 0 data command");
        Check(parsed.Data.SequenceEqual(new byte[] { 0x01, 0x02, 0x03 }), "built KISS frame preserves AX.25 bytes");
    }

    private static void TestKissEscaping()
    {
        byte[] frame = KissFrameBuilder.BuildDataFrame(new byte[] { 0xC0, 0xDB, 0x55 });
        Check(frame.SequenceEqual(new byte[] { 0xC0, 0x00, 0xDB, 0xDC, 0xDB, 0xDD, 0x55, 0xC0 }), "KISS builder escapes FEND/FESC correctly");
        KissFrame parsed = new KissStreamDecoder().Push(frame).Single();
        Check(parsed.Data.SequenceEqual(new byte[] { 0xC0, 0xDB, 0x55 }), "KISS decoder reverses escaped FEND/FESC");
        Check(!parsed.IsMalformed, "valid KISS escapes remain warning-free");
    }

    private static void TestKissProcessingPolicyGate()
    {
        var decoder = new KissStreamDecoder();
        byte[] frame = { 0xC0, 0x00, 0x41, 0xC0 };

        KissStreamDecoder.ProcessingPolicy = d => !ReferenceEquals(d, decoder);
        Check(!decoder.Push(frame).Any(), "disabled KISS policy applies no parsing");

        KissStreamDecoder.ProcessingPolicy = _ => true;
        Check(decoder.Push(frame).Single().Data.SequenceEqual(new byte[] { 0x41 }), "enabled KISS policy parses same raw stream");
        KissStreamDecoder.ProcessingPolicy = null;
    }

    private static async Task TestUnboundedQueueOrderingAsync()
    {
        const int commandCount = 64;
        var queue = new SerialTaskQueue();
        var sent = new List<int>();
        var tasks = new List<Task>();

        for (int i = 0; i < commandCount; i++)
        {
            int id = i;
            tasks.Add(queue.Enqueue(async () =>
            {
                await Task.Delay(id % 3);
                sent.Add(id);
            }));
        }

        await Task.WhenAll(tasks);
        Check(sent.Count == commandCount, "command queue accepts more than five independent sends");
        Check(sent.SequenceEqual(Enumerable.Range(0, commandCount)), "rapid command payloads remain separate and ordered beyond five rows");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"FAIL: {name}");
        _passed++;
        Console.WriteLine($"PASS  {name}");
    }
}
