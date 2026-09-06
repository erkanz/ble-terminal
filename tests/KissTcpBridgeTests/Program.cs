using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BLESerialTerminal;

static class Test
{
    private static int _checks;
    private static readonly ConcurrentQueue<byte[]> BleWrites = new();
    private static readonly ConcurrentQueue<KissTcpBridgeEvent> Events = new();

    public static async Task<int> Main()
    {
        try
        {
            CheckThrows(() => new KissTcpBridgeOptions(IPAddress.Loopback, -1).Validate(), "invalid negative port rejected");
            CheckThrows(() => new KissTcpBridgeOptions(IPAddress.Loopback, 70000).Validate(), "invalid high port rejected");
            CheckThrows(() => new KissTcpBridgeOptions(IPAddress.Loopback, 8001, ClientQueueCapacity: 0).Validate(), "invalid queue capacity rejected");

            await using var server = new KissTcpBridgeServer(async (bytes, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                BleWrites.Enqueue(bytes.ToArray());
                await Task.Yield();
                return KissTcpBridgeBleWriteResult.Ok(bytes.Length);
            });
            server.EventOccurred += evt => Events.Enqueue(evt);

            await server.StartAsync(new KissTcpBridgeOptions(IPAddress.Loopback, 0, ClientQueueCapacity: 8));
            KissTcpBridgeStats stats = server.GetStats();
            Check(stats.Running, "loopback bridge starts");
            Check(stats.Endpoint.StartsWith("127.0.0.1:", StringComparison.Ordinal), "default test listener is loopback");

            server.SetBleAvailable(true, "connection-A", "test route");
            Check(server.GetStats().BleAvailable, "BLE forwarding can become ready");

            IPEndPoint endpoint = IPEndPoint.Parse(server.GetStats().Endpoint);
            using TcpClient client1 = new();
            await client1.ConnectAsync(endpoint.Address, endpoint.Port);
            await WaitUntilAsync(() => server.GetStats().Clients == 1, "first TCP client connect");
            Check(server.GetStats().Clients == 1, "first TCP client is tracked");

            byte[] bleFrame1 = [0xC0, 0x00, 0x01, 0x02, 0xC0];
            server.BroadcastBleRx(bleFrame1);
            byte[] received1 = await ReadExactAsync(client1.GetStream(), bleFrame1.Length);
            Check(received1.SequenceEqual(bleFrame1), "BLE->TCP preserves raw KISS bytes exactly");
            await WaitUntilAsync(() => server.GetStats().TcpTxBytes >= bleFrame1.Length, "BLE->TCP byte counter");
            stats = server.GetStats();
            Check(stats.BleRxBytes == bleFrame1.Length, "BLE RX byte counter increments once per BLE stream byte");
            Check(stats.BleRxFrames == 1, "BLE RX KISS frame counter handles a complete frame");
            Check(stats.TcpTxBytes == bleFrame1.Length, "TCP outbound byte counter reports delivered bytes");

            byte[] tcpPart1 = [0xC0, 0x00, 0x10];
            byte[] tcpPart2 = [0x11, 0xC0];
            await client1.GetStream().WriteAsync(tcpPart1);
            await client1.GetStream().WriteAsync(tcpPart2);
            await WaitUntilAsync(() => server.GetStats().BleTxBytes >= tcpPart1.Length + tcpPart2.Length, "TCP->BLE write completion");
            byte[] tcpCombined = BleWrites.SelectMany(x => x).ToArray();
            Check(tcpCombined.SequenceEqual(tcpPart1.Concat(tcpPart2)), "TCP stream bytes reach BLE writer unchanged across arbitrary reads");
            stats = server.GetStats();
            Check(stats.TcpRxBytes == tcpPart1.Length + tcpPart2.Length, "TCP RX byte counter matches host input");
            Check(stats.BleTxBytes == tcpPart1.Length + tcpPart2.Length, "BLE TX byte counter counts successful writes only");
            Check(stats.TcpRxFrames == 1, "TCP KISS frame diagnostics reassemble fragmented input");

            using TcpClient client2 = new();
            await client2.ConnectAsync(endpoint.Address, endpoint.Port);
            await WaitUntilAsync(() => server.GetStats().Clients == 2, "second TCP client connect");
            byte[] bleFrame2 = [0xC0, 0x00, 0x22, 0xC0];
            server.BroadcastBleRx(bleFrame2);
            byte[] received2a = await ReadExactAsync(client1.GetStream(), bleFrame2.Length);
            byte[] received2b = await ReadExactAsync(client2.GetStream(), bleFrame2.Length);
            Check(received2a.SequenceEqual(bleFrame2) && received2b.SequenceEqual(bleFrame2), "BLE RX fans out identically to multiple TCP clients");
            await WaitUntilAsync(() => server.GetStats().TcpTxBytes >= bleFrame1.Length + bleFrame2.Length * 2, "multi-client byte accounting");
            Check(server.GetStats().Clients == 2, "multiple clients stay independent");

            byte[] malformed = [0xC0, 0x00, 0xDB, 0x01, 0xC0];
            await client1.GetStream().WriteAsync(malformed);
            await WaitUntilAsync(() => server.GetStats().MalformedFrames >= 1, "malformed KISS diagnostic");
            Check(server.GetStats().MalformedFrames >= 1, "malformed KISS is counted but raw bytes are still forwarded");
            Check(Events.Any(e => e.Kind == KissTcpBridgeEventKind.Warning && e.Message.Contains("Malformed TCP->BLE", StringComparison.Ordinal)), "malformed KISS produces diagnostic warning");

            server.SetBleAvailable(true, "connection-B", "fresh reconnect context");
            await WaitUntilAsync(() => server.GetStats().Clients == 0, "fresh BLE context closes old TCP clients");
            Check(server.GetStats().BleAvailable, "new BLE connection context remains ready after old clients close");
            Check(server.GetStats().Clients == 0, "stale TCP clients are not reused across BLE reconnect contexts");

            using TcpClient client3 = new();
            await client3.ConnectAsync(endpoint.Address, endpoint.Port);
            await WaitUntilAsync(() => server.GetStats().Clients == 1, "client reconnect on fresh BLE context");
            server.SetBleAvailable(false, string.Empty, "BLE disconnected");
            await WaitUntilAsync(() => server.GetStats().Clients == 0, "BLE disconnect closes clients");
            Check(!server.GetStats().BleAvailable, "BLE disconnect suspends forwarding");
            Check(server.GetStats().Clients == 0, "BLE disconnect leaves no live TCP clients");

            await server.StopAsync();
            stats = server.GetStats();
            Check(!stats.Running, "bridge stops cleanly");
            Check(stats.Endpoint == "-", "listener endpoint is cleared on stop");

            Console.WriteLine();
            Console.WriteLine($"KISS TCP BRIDGE TESTS: PASS ({_checks} checks)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("KISS TCP BRIDGE TESTS: FAIL");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        byte[] result = new byte[count];
        int offset = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (offset < count)
        {
            int read = await stream.ReadAsync(result.AsMemory(offset, count - offset), cts.Token);
            if (read == 0)
                throw new IOException($"TCP stream closed after {offset}/{count} bytes.");
            offset += read;
        }
        return result;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string description)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"FAIL  {description}");
        _checks++;
        Console.WriteLine($"PASS  {description}");
    }

    private static void CheckThrows(Action action, string description)
    {
        try
        {
            action();
        }
        catch
        {
            Check(true, description);
            return;
        }
        Check(false, description);
    }
}

return await Test.Main();
