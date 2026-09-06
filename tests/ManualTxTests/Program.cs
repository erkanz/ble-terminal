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
