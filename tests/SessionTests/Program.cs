using System.Globalization;
using BLESerialTerminal;

int checks = 0;

void Check(bool condition, string name)
{
    checks++;
    if (!condition)
        throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS  {name}");
}

byte[] HexBytes(string hex) =>
    hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
       .Select(x => byte.Parse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
       .ToArray();

try
{
    var store = new LogStore(1000);
    using var recorder = new SessionRecorder(store);
    var metadata = new SessionMetadata
    {
        ApplicationVersion = "test-1.0",
        Device = "RT-950",
        Address = "20:6E:F1:A7:51:4D",
        ConnectionState = "Connected",
        Profile = "RADTEL_RT950_KISS",
        ServiceUuid = "0000FFE0-0000-1000-8000-00805F9B34FB",
        WriteUuid = "0000FFE1-0000-1000-8000-00805F9B34FB",
        NotifyUuid = "0000FFE1-0000-1000-8000-00805F9B34FB",
        SameCharacteristic = true,
        RelevantGatt = new List<SessionGattCharacteristic>
        {
            new()
            {
                Role = "WRITE+NOTIFY",
                ServiceUuid = "0000FFE0-0000-1000-8000-00805F9B34FB",
                CharacteristicUuid = "0000FFE1-0000-1000-8000-00805F9B34FB",
                Properties = "Write, Notify"
            }
        }
    };

    byte[] kissPayload = HexBytes(
        "00 82 A0 82 A8 70 62 E2 96 8A 64 84 A6 88 6E AE 92 88 8A 62 40 62 AE 92 88 8A 64 40 63 03 F0 21 33 34 31 32 2E 37 33 4E 2F 31 30 38 34 39 2E 3A 30 45 26 33 34 38 2F 30 30 31 2F 41 3D 30 30 30 30 30 30 41 50 52 53 43 4E 20 57 49 46 49 20 34 2E 33 30 56");
    byte[] kissFrame = new byte[kissPayload.Length + 2];
    kissFrame[0] = 0xC0;
    System.Buffer.BlockCopy(kissPayload, 0, kissFrame, 1, kissPayload.Length);
    kissFrame[^1] = 0xC0;
    byte[] part1 = kissFrame[..20];
    byte[] part2 = kissFrame[20..];

    recorder.Start(metadata);
    store.Add(LogCategory.CONNECTION, "CONNECTED RT-950", device: "RT-950");
    store.Add(LogCategory.RX_RAW, "fragment 1", "RX", "RT-950", "FFE1", data: part1);
    store.Add(LogCategory.TX_RAW, "test tx", "TX", "RT-950", "FFE1", data: new byte[] { 0x41, 0x54, 0x0A });
    store.Add(LogCategory.RX_RAW, "fragment 2", "RX", "RT-950", "FFE1", data: part2);
    recorder.Stop();

    SessionDocument document = recorder.Snapshot();
    Check(document.Format == SessionFormat.Magic && document.Version == 1, "session format/version");
    Check(document.Events.Count == 4, "recorder captures structured events");
    Check(document.Metadata.Profile == "RADTEL_RT950_KISS" && document.Metadata.SameCharacteristic, "capture metadata preserved");
    Check(document.Metadata.CaptureStartedUtc != default && document.Metadata.CaptureEndedUtc.HasValue, "capture start/end timestamps");
    Check(document.Events[1].GetData().SequenceEqual(part1), "raw RX bytes preserved structurally");
    Check(document.Events[2].GetData().SequenceEqual(new byte[] { 0x41, 0x54, 0x0A }), "raw TX bytes preserved structurally");

    string json = SessionSerializer.Serialize(document);
    SessionDocument roundTrip = SessionSerializer.Deserialize(json);
    Check(roundTrip.Events.Count == document.Events.Count, "session JSON round trip event count");
    Check(roundTrip.Events[3].GetData().SequenceEqual(part2), "session JSON round trip binary payload");
    Check(roundTrip.Metadata.RelevantGatt.Count == 1, "relevant GATT metadata round trip");

    string withUnknownFutureField = json.Replace("\"Version\": 1,", "\"Version\": 1,\n  \"FutureFieldIgnoredByV1\": { \"x\": 123 },", StringComparison.Ordinal);
    SessionDocument unknownFieldDoc = SessionSerializer.Deserialize(withUnknownFutureField);
    Check(unknownFieldDoc.Events.Count == 4, "unknown JSON fields are safely ignored");

    bool malformedRejected = false;
    try { _ = SessionSerializer.Deserialize("{ not-json"); } catch (InvalidDataException) { malformedRejected = true; }
    Check(malformedRejected, "malformed session fails gracefully");

    bool futureVersionRejected = false;
    try
    {
        string future = json.Replace("\"Version\": 1", "\"Version\": 99", StringComparison.Ordinal);
        _ = SessionSerializer.Deserialize(future);
    }
    catch (NotSupportedException) { futureVersionRejected = true; }
    Check(futureVersionRejected, "unsupported future session version rejected safely");

    var replay = new SessionReplayProcessor();
    var decoded = new List<ReplayDecodedPacket>();
    foreach (SessionEventRecord record in roundTrip.Events)
        decoded.AddRange(replay.Process(record));

    Check(replay.RawRxEvents == 2 && replay.RawRxBytes == kissFrame.Length, "offline replay consumes only captured raw RX events");
    Check(replay.KissFrames == 1 && decoded.Count == 1, "fragmented KISS frame reassembles during offline replay");
    Ax25Packet? replayAx25 = decoded[0].Ax25;
    AprsPacket? replayAprs = decoded[0].Aprs;
    Check(replayAx25 != null && replayAx25.FrameType == "UI" && replayAx25.Pid == 0xF0, "offline replay AX.25 decode");
    Check(replayAprs != null && replayAprs.RawInformation.StartsWith("!3412.73N/", StringComparison.Ordinal), "offline replay APRS result matches RT-950 fixture");

    string firstSummary = decoded[0].Summary;
    replay.Reset();
    decoded.Clear();
    foreach (SessionEventRecord record in roundTrip.Events)
        decoded.AddRange(replay.Process(record));
    Check(decoded.Count == 1 && decoded[0].Summary == firstSummary, "offline replay is deterministic after reset");

    var txOnlyReplay = new SessionReplayProcessor();
    IReadOnlyList<ReplayDecodedPacket> txDecoded = txOnlyReplay.Process(roundTrip.Events.Single(x => x.Category == "TX_RAW"));
    Check(txDecoded.Count == 0 && txOnlyReplay.RawRxEvents == 0, "captured TX is never fed into offline RX replay pipeline");

    recorder.Start(metadata);
    store.Add(LogCategory.GATT, "second capture");
    recorder.Stop();
    Check(recorder.Snapshot().Events.Count == 1, "new capture starts with fresh event list");

    Console.WriteLine($"\nSESSION TESTS: PASS ({checks} checks)");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("SESSION TESTS: FAIL");
    Console.Error.WriteLine(ex.ToString());
    return 1;
}
