using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private enum Rt950Rtx1TestState
    {
        Idle,
        ProtocolReady,
        NotifyActive,
        WaitHandshakeNotification,
        HandshakeComplete,
        SendRtx1Chunks,
        Rtx1Sent,
        WaitDiagnosticNotification,
        Complete,
        Failed
    }

    private int _rtx1TestInProgress;
    private int _rtx1FrameCounter;
    private int _rtx1TestStateValue;
    private bool _rtx1UiPrepared;
    private Button? _runRt950Rtx1TestButton;
    private TaskCompletionSource<NotificationRecord>? _rtx1HandshakeNotificationTcs;
    private TaskCompletionSource<NotificationRecord>? _rtx1DiagnosticNotificationTcs;

    private Rt950Rtx1TestState Rtx1TestState
    {
        get => (Rt950Rtx1TestState)Volatile.Read(ref _rtx1TestStateValue);
        set => Volatile.Write(ref _rtx1TestStateValue, (int)value);
    }

    internal void PrepareRt950Rtx1AutoUi()
    {
        if (_rtx1UiPrepared)
            return;
        _rtx1UiPrepared = true;

        RemoveLegacyRt950CommandRowsFromNormalWorkspace();
        ReplaceLegacyRt950Buttons();
        RebuildRt950MenuForAutoTest();
        GattInspectorButton.IsEnabledChanged += Rt950Rtx1ConnectionStateChanged;
        ValidateRt950Rtx1ProtocolOnConnect();
    }

    private void ReplaceLegacyRt950Buttons()
    {
        Button? first = FindButtonByContent(this, "RT950 OEM TEST");
        Button? second = FindButtonByContent(this, "RT950 FFE1 TEST");
        Button? load = FindButtonByContent(this, "Load RT950 Test Commands");
        if (first?.Parent is not Panel panel)
            return;

        int insertIndex = panel.Children.IndexOf(first);
        if (first.Parent == panel) panel.Children.Remove(first);
        if (second?.Parent == panel) panel.Children.Remove(second);
        if (load?.Parent == panel) panel.Children.Remove(load);

        _runRt950Rtx1TestButton = new Button
        {
            Content = "Run RTX1 TX Test",
            Width = 170,
            Height = 28,
            Margin = new Thickness(0, 0, 10, 0)
        };
        _runRt950Rtx1TestButton.Click += RunRt950Rtx1TestButton_Click;
        panel.Children.Insert(Math.Max(0, insertIndex), _runRt950Rtx1TestButton);
    }

    private static Button? FindButtonByContent(DependencyObject root, string content)
    {
        if (root is Button button && string.Equals(button.Content?.ToString(), content, StringComparison.Ordinal))
            return button;

        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject dependencyObject)
            {
                Button? match = FindButtonByContent(dependencyObject, content);
                if (match != null)
                    return match;
            }
        }
        return null;
    }

    private void Rt950Rtx1ConnectionStateChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        ValidateRt950Rtx1ProtocolOnConnect();

    private void ValidateRt950Rtx1ProtocolOnConnect()
    {
        if (!_bleConnected || !GattInspectorButton.IsEnabled || _service == null ||
            !BleUuid.Is(_service.Uuid, Rt950Rtx1Protocol.ServiceUuid))
            return;

        if (TryResolveRt950Rtx1Transport(out _, out _, out string reason))
        {
            AppendSystemLine("*** RTX1 PROTOCOL ASSERTIONS PASS");
            AppendSystemLine("*** SERVICE=FFE0");
            AppendSystemLine("*** FF31_WRITE_WITH_RESPONSE=YES");
            AppendSystemLine("*** FFE1_WRITE_WITH_RESPONSE=YES");
            AppendSystemLine("*** FFE1_NOTIFY=YES");
        }
        else
        {
            AppendSystemLine("*** RTX1 PROTOCOL_ERROR");
            AppendSystemLine($"*** REASON={reason}");
        }
    }

    private void RemoveLegacyRt950CommandRowsFromNormalWorkspace()
    {
        int removed = _commandRows.RemoveAll(row =>
            string.Equals(row.ModuleTag, "RT950", StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return;

        if (_commandRows.Count == 0)
            _commandRows.Add(NewGeneralCommand("Command 1"));
        RebuildDynamicCommandRows();
        SaveTxCommandPreferences();
        AppendSystemLine($"RT950 LEGACY MANUAL COMMANDS HIDDEN removed={removed}; auto RTX1 test is the normal RT950 path");
    }

    private void RebuildRt950MenuForAutoTest()
    {
        if (_rt950Menu == null)
            return;

        var run = new MenuItem { Header = "Run RTX1 TX Test" };
        run.Click += async (_, _) => await RunRt950Rtx1TestAsync(1);

        var developer = new MenuItem { Header = "Developer / Legacy Diagnostics" };
        MenuItem?[] legacyItems =
        {
            _rt950EnableMenuItem,
            _rt950PresetMenuItem,
            _rt950UnlockMenuItem,
            _rt950AutoUnlockMenuItem,
            _rt950DiagnosticsMenuItem
        };

        _rt950Menu.Items.Clear();
        _rt950Menu.Items.Add(run);
        _rt950Menu.Items.Add(new Separator());
        foreach (MenuItem? item in legacyItems)
        {
            if (item != null)
                developer.Items.Add(item);
        }
        _rt950Menu.Items.Add(developer);
    }

    private async void RunRt950Rtx1TestButton_Click(object sender, RoutedEventArgs e) =>
        await RunRt950Rtx1TestAsync(1);

    internal async Task RunRt950Rtx1TestAsync(int count)
    {
        if (count is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(count), "RTX1 repeat count must be 1 through 99.");

        if (Interlocked.CompareExchange(ref _rtx1TestInProgress, 1, 0) != 0)
        {
            AppendSystemLine("*** RTX1 AUTO TEST REJECTED");
            AppendSystemLine("*** REASON=TEST_ALREADY_IN_PROGRESS");
            return;
        }

        if (_runRt950Rtx1TestButton != null)
            _runRt950Rtx1TestButton.IsEnabled = false;

        NotificationCaptureHub.Store.RecordAdded += Rt950Rtx1Notification_RecordAdded;
        try
        {
            Task queued = _manualTxQueue.Enqueue(() =>
                Dispatcher.InvokeAsync(() => ExecuteRt950Rtx1SequenceAsync(count)).Task.Unwrap());
            await queued;
        }
        catch (Exception ex)
        {
            AppendSystemLine("*** RTX1 AUTO TEST COMPLETE");
            AppendSystemLine($"*** INTERNAL_ERROR={ex.Message}");
            AppendSystemLine("*** BLE_TRANSPORT=FAIL");
            AppendSystemLine("*** RTX1_DEVICE_ACK=NOT_SEEN");
            AppendSystemLine("*** RF_TX=UNKNOWN");
        }
        finally
        {
            NotificationCaptureHub.Store.RecordAdded -= Rt950Rtx1Notification_RecordAdded;
            _rtx1HandshakeNotificationTcs?.TrySetCanceled();
            _rtx1DiagnosticNotificationTcs?.TrySetCanceled();
            _rtx1HandshakeNotificationTcs = null;
            _rtx1DiagnosticNotificationTcs = null;
            Rtx1TestState = Rt950Rtx1TestState.Idle;
            Interlocked.Exchange(ref _rtx1TestInProgress, 0);
            if (_runRt950Rtx1TestButton != null)
                _runRt950Rtx1TestButton.IsEnabled = true;
        }
    }

    private async Task ExecuteRt950Rtx1SequenceAsync(int count)
    {
        for (int i = 0; i < count; i++)
        {
            int counter = Interlocked.Increment(ref _rtx1FrameCounter);
            if (counter > 9999)
            {
                Interlocked.Exchange(ref _rtx1FrameCounter, 1);
                counter = 1;
            }

            bool transportComplete = await ExecuteRt950Rtx1FrameAsync(counter);
            if (!transportComplete)
                break;

            if (i + 1 < count)
                await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private async Task<bool> ExecuteRt950Rtx1FrameAsync(int counter)
    {
        bool handshakePass = false;
        bool fcsPass = false;
        bool chunksPass = false;
        bool diagnosticSeen = false;
        ulong? sessionAddress = _connectedAddress;
        DateTime? sessionConnectedAt = _connectedAt;
        string information = Rt950Rtx1Protocol.InformationText(counter);

        LogRt950Rtx1Start(information);

        if (!_bleConnected || !_servicesDiscovered || _service == null ||
            !sessionAddress.HasValue || !sessionConnectedAt.HasValue)
        {
            LogRt950ProtocolFailure("BLE_NOT_CONNECTED_OR_DISCOVERY_INCOMPLETE", handshakePass, fcsPass);
            return false;
        }

        if (!TryResolveRt950Rtx1Transport(out GattCharacteristic? ffe1, out GattCharacteristic? ff31, out string reason) ||
            ffe1 == null || ff31 == null)
        {
            LogRt950ProtocolFailure(reason, handshakePass, fcsPass);
            return false;
        }
        Rtx1TestState = Rt950Rtx1TestState.ProtocolReady;

        if (!await EnsureRt950Rtx1NotificationsAsync(ffe1) || !IsSameConnectionSession(sessionAddress, sessionConnectedAt))
        {
            LogRt950ProtocolFailure("FFE1_NOTIFICATION_SUBSCRIBE_FAILED", handshakePass, fcsPass);
            return false;
        }
        Rtx1TestState = Rt950Rtx1TestState.NotifyActive;

        AppendSystemLine("*** OEM HANDSHAKE");
        AppendSystemLine("*** SERVICE=FFE0");
        AppendSystemLine("*** UUID=FF31");
        AppendSystemLine("*** TYPE=WITH_RESPONSE");
        AppendSystemLine($"*** LEN={Rt950Rtx1Protocol.OemHandshake.Length}");
        AppendSystemLine($"*** HEX={Hex(Rt950Rtx1Protocol.OemHandshake)}");

        var handshakeTcs = new TaskCompletionSource<NotificationRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rtx1HandshakeNotificationTcs = handshakeTcs;
        Rtx1TestState = Rt950Rtx1TestState.WaitHandshakeNotification;

        GattWriteResult? handshakeWrite = await WriteRt950Rtx1WithResponseAsync(
            ff31,
            Rt950Rtx1Protocol.OemHandshake,
            sessionAddress,
            sessionConnectedAt,
            "HANDSHAKE");

        if (handshakeWrite?.Status != GattCommunicationStatus.Success)
        {
            AppendSystemLine("*** HANDSHAKE=FAIL");
            AppendSystemLine("*** RTX1_SEND=SKIPPED");
            LogRt950Rtx1Final(handshakePass, fcsPass, chunksPass, diagnosticSeen, transportPass: false);
            Rtx1TestState = Rt950Rtx1TestState.Failed;
            return false;
        }

        Task handshakeCompleted = await Task.WhenAny(handshakeTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        if (!IsSameConnectionSession(sessionAddress, sessionConnectedAt))
        {
            AppendSystemLine("*** HANDSHAKE=FAIL");
            AppendSystemLine("*** REASON=CONNECTION_LOST");
            AppendSystemLine("*** RTX1_SEND=SKIPPED");
            LogRt950Rtx1Final(handshakePass, fcsPass, chunksPass, diagnosticSeen, transportPass: false);
            Rtx1TestState = Rt950Rtx1TestState.Failed;
            return false;
        }
        if (handshakeCompleted != handshakeTcs.Task || handshakeTcs.Task.IsCanceled || handshakeTcs.Task.IsFaulted)
        {
            AppendSystemLine("*** HANDSHAKE=FAIL");
            AppendSystemLine("*** REASON=FFE1_NOTIFICATION_TIMEOUT");
            AppendSystemLine("*** RTX1_SEND=SKIPPED");
            LogRt950Rtx1Final(handshakePass, fcsPass, chunksPass, diagnosticSeen, transportPass: false);
            Rtx1TestState = Rt950Rtx1TestState.Failed;
            return false;
        }

        NotificationRecord handshakeNotification = await handshakeTcs.Task;
        AppendSystemLine("*** HANDSHAKE NOTIFICATION");
        AppendSystemLine("*** UUID=FFE1");
        AppendSystemLine($"*** LEN={handshakeNotification.Length}");
        AppendSystemLine($"*** HEX={handshakeNotification.Hex}");
        handshakePass = true;
        Rtx1TestState = Rt950Rtx1TestState.HandshakeComplete;

        byte[] packet;
        byte[] ax25Raw;
        ushort fcs;
        try
        {
            packet = Rt950Rtx1Protocol.BuildRtx1Packet(counter, out ax25Raw, out fcs);
        }
        catch (Exception ex)
        {
            AppendSystemLine("*** AX25 FCS");
            AppendSystemLine($"*** VERIFY=FAIL ({ex.Message})");
            AppendSystemLine("*** RTX1_SEND=SKIPPED");
            LogRt950Rtx1Final(handshakePass, fcsPass, chunksPass, diagnosticSeen, transportPass: false);
            Rtx1TestState = Rt950Rtx1TestState.Failed;
            return false;
        }

        byte[] wireFcs = Rt950Rtx1Protocol.FcsWireBytes(fcs);
        fcsPass = Rt950Rtx1Protocol.VerifyFcs(ax25Raw, wireFcs);
        AppendSystemLine("*** AX25 RAW");
        AppendSystemLine($"*** LEN={ax25Raw.Length}");
        AppendSystemLine($"*** HEX={Hex(ax25Raw)}");
        AppendSystemLine($"*** AX25_RAW_LEN={ax25Raw.Length}");
        AppendSystemLine($"*** AX25_RAW_HEX={Hex(ax25Raw)}");
        AppendSystemLine("*** AX25 FCS");
        AppendSystemLine($"*** CALCULATED=0x{fcs:X4}");
        AppendSystemLine($"*** WIRE={Hex(wireFcs)}");
        AppendSystemLine($"*** VERIFY={(fcsPass ? "PASS" : "FAIL")}");
        AppendSystemLine($"*** AX25_FCS_CALC=0x{fcs:X4}");
        AppendSystemLine($"*** AX25_FCS_WIRE={Hex(wireFcs)}");
        AppendSystemLine($"*** AX25_FCS_VERIFY={(fcsPass ? "PASS" : "FAIL")}");
        if (!fcsPass)
        {
            AppendSystemLine("*** RTX1_SEND=SKIPPED");
            LogRt950Rtx1Final(handshakePass, fcsPass, chunksPass, diagnosticSeen, transportPass: false);
            Rtx1TestState = Rt950Rtx1TestState.Failed;
            return false;
        }

        IReadOnlyList<byte[]> chunks = Rt950Rtx1Protocol.SplitChunks(packet);
        AppendSystemLine("*** RTX1 PACKET");
        AppendSystemLine($"*** LEN={packet.Length}");
        AppendSystemLine($"*** HEX={Hex(packet)}");
        AppendSystemLine($"*** RTX1_TOTAL_LEN={packet.Length}");
        AppendSystemLine($"*** RTX1_FULL_HEX={Hex(packet)}");

        var diagnosticTcs = new TaskCompletionSource<NotificationRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rtx1DiagnosticNotificationTcs = diagnosticTcs;
        Rtx1TestState = Rt950Rtx1TestState.SendRtx1Chunks;

        int failedChunk = 0;
        var stopwatch = Stopwatch.StartNew();
        DateTime firstChunkTime = DateTime.Now;
        DateTime lastChunkTime = firstChunkTime;

        await _gattOperationGate.WaitAsync();
        try
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                if (!IsSameConnectionSession(sessionAddress, sessionConnectedAt))
                {
                    failedChunk = i + 1;
                    AppendSystemLine("*** RTX1_SEND=FAIL");
                    AppendSystemLine("*** REASON=CONNECTION_LOST");
                    break;
                }

                byte[] chunk = chunks[i];
                if (i == 0)
                    firstChunkTime = DateTime.Now;
                AppendSystemLine($"*** RTX1 CHUNK {i + 1}/{chunks.Count}");
                AppendSystemLine("*** SERVICE=FFE0");
                AppendSystemLine("*** UUID=FFE1");
                AppendSystemLine("*** TYPE=WITH_RESPONSE");
                AppendSystemLine($"*** LEN={chunk.Length}");
                AppendSystemLine($"*** HEX={Hex(chunk)}");

                GattWriteResult? result = null;
                try
                {
                    using var writer = new DataWriter();
                    writer.WriteBytes(chunk);
                    IBuffer buffer = writer.DetachBuffer();
                    result = await ffe1.WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithResponse);
                }
                catch (Exception ex)
                {
                    AppendSystemLine("*** WRITE COMPLETE=FAIL");
                    AppendSystemLine($"*** HRESULT=0x{ex.HResult:X8}");
                    AppendSystemLine($"*** EXCEPTION={ex.Message}");
                }

                bool success = result?.Status == GattCommunicationStatus.Success;
                if (result != null)
                {
                    AppendSystemLine($"*** WRITE COMPLETE={(success ? "SUCCESS" : "FAIL")}");
                    AppendSystemLine($"*** STATUS_CODE={(int)result.Status}");
                    AppendSystemLine($"*** PROTOCOL_ERROR={ProtocolText(result.ProtocolError)}");
                }
                if (!success)
                {
                    failedChunk = i + 1;
                    break;
                }
                lastChunkTime = DateTime.Now;
            }
        }
        finally
        {
            _gattOperationGate.Release();
            stopwatch.Stop();
        }

        if (failedChunk != 0)
        {
            AppendSystemLine("*** RTX1_SEND=FAIL");
            AppendSystemLine($"*** FAILED_CHUNK={failedChunk}/{chunks.Count}");
            LogRt950Rtx1Final(handshakePass, fcsPass, chunksPass, diagnosticSeen, transportPass: false);
            Rtx1TestState = Rt950Rtx1TestState.Failed;
            return false;
        }

        chunksPass = true;
        Rtx1TestState = Rt950Rtx1TestState.Rtx1Sent;
        AppendSystemLine($"*** RTX1_FIRST_CHUNK_TIME={firstChunkTime:HH:mm:ss.fff}");
        AppendSystemLine($"*** RTX1_LAST_CHUNK_TIME={lastChunkTime:HH:mm:ss.fff}");
        AppendSystemLine($"*** RTX1_SEND_DURATION_MS={stopwatch.Elapsed.TotalMilliseconds:F1}");

        Interlocked.Add(ref _txBytes, packet.Length);
        UpdateCounters();

        Rtx1TestState = Rt950Rtx1TestState.WaitDiagnosticNotification;
        Task diagnosticCompleted = await Task.WhenAny(diagnosticTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        diagnosticSeen = diagnosticCompleted == diagnosticTcs.Task &&
            !diagnosticTcs.Task.IsCanceled && !diagnosticTcs.Task.IsFaulted;

        Rtx1TestState = Rt950Rtx1TestState.Complete;
        LogRt950Rtx1Final(handshakePass, fcsPass, chunksPass, diagnosticSeen, transportPass: true);
        return true;
    }

    private bool TryResolveRt950Rtx1Transport(
        out GattCharacteristic? ffe1,
        out GattCharacteristic? ff31,
        out string reason)
    {
        ffe1 = null;
        ff31 = null;
        reason = string.Empty;

        if (!_servicesDiscovered)
        {
            reason = "CHARACTERISTIC_DISCOVERY_NOT_COMPLETE";
            return false;
        }
        if (_service == null || !BleUuid.Is(_service.Uuid, Rt950Rtx1Protocol.ServiceUuid))
        {
            reason = "FFE0_SERVICE_NOT_FOUND";
            return false;
        }

        List<GattCharacteristic> characteristics = CurrentServiceCharacteristics();
        ff31 = characteristics.FirstOrDefault(c =>
            BleUuid.Is(c.Uuid, Rt950Rtx1Protocol.HandshakeCharacteristicUuid) &&
            c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write));
        if (ff31 == null)
        {
            reason = "FF31_WRITE_WITH_RESPONSE_NOT_AVAILABLE";
            return false;
        }

        ffe1 = characteristics.FirstOrDefault(c => BleUuid.Is(c.Uuid, Rt950Rtx1Protocol.Rtx1CharacteristicUuid));
        if (ffe1 == null)
        {
            reason = "FFE1_CHARACTERISTIC_NOT_FOUND";
            return false;
        }
        if (!ffe1.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write))
        {
            reason = "FFE1_WRITE_WITH_RESPONSE_NOT_AVAILABLE";
            return false;
        }
        if (!ffe1.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
        {
            reason = "FFE1_NOTIFY_NOT_AVAILABLE";
            return false;
        }
        return true;
    }

    private async Task<bool> EnsureRt950Rtx1NotificationsAsync(GattCharacteristic ffe1)
    {
        if (_notifyCharacteristic != null &&
            _notifyCharacteristic.Uuid == ffe1.Uuid &&
            _ffe1CccdEnabled)
            return true;

        await SwitchNotifyCharacteristicAsync(ffe1);
        return _notifyCharacteristic != null &&
            _notifyCharacteristic.Uuid == ffe1.Uuid &&
            _ffe1CccdEnabled;
    }

    private async Task<GattWriteResult?> WriteRt950Rtx1WithResponseAsync(
        GattCharacteristic characteristic,
        byte[] payload,
        ulong? sessionAddress,
        DateTime? sessionConnectedAt,
        string operation)
    {
        await _gattOperationGate.WaitAsync();
        try
        {
            if (!IsSameConnectionSession(sessionAddress, sessionConnectedAt))
                return null;

            using var writer = new DataWriter();
            writer.WriteBytes(payload);
            IBuffer buffer = writer.DetachBuffer();
            GattWriteResult result = await characteristic.WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithResponse);
            AppendSystemLine($"*** {operation} WRITE COMPLETE={(result.Status == GattCommunicationStatus.Success ? "SUCCESS" : "FAIL")}");
            AppendSystemLine($"*** STATUS_CODE={(int)result.Status}");
            AppendSystemLine($"*** PROTOCOL_ERROR={ProtocolText(result.ProtocolError)}");
            return result;
        }
        catch (Exception ex)
        {
            AppendSystemLine($"*** {operation} WRITE COMPLETE=FAIL");
            AppendSystemLine($"*** HRESULT=0x{ex.HResult:X8}");
            AppendSystemLine($"*** EXCEPTION={ex.Message}");
            return null;
        }
        finally
        {
            _gattOperationGate.Release();
        }
    }

    private void Rt950Rtx1Notification_RecordAdded(NotificationRecord record)
    {
        if (Volatile.Read(ref _rtx1TestInProgress) == 0 ||
            !BleUuid.Is(record.CharacteristicUuid, Rt950Rtx1Protocol.Rtx1CharacteristicUuid))
            return;

        _ = Dispatcher.BeginInvoke(() =>
        {
            AppendSystemLine("*** RAW BLE NOTIFICATION");
            AppendSystemLine($"*** TIME={record.Timestamp:HH:mm:ss.fff}");
            AppendSystemLine("*** UUID=FFE1");
            AppendSystemLine($"*** LEN={record.Length}");
            AppendSystemLine($"*** HEX={record.Hex}");
        });

        Rt950Rtx1TestState state = Rtx1TestState;
        if (state == Rt950Rtx1TestState.WaitHandshakeNotification)
            _rtx1HandshakeNotificationTcs?.TrySetResult(record);
        else if (state is Rt950Rtx1TestState.SendRtx1Chunks or Rt950Rtx1TestState.Rtx1Sent or Rt950Rtx1TestState.WaitDiagnosticNotification)
            _rtx1DiagnosticNotificationTcs?.TrySetResult(record);
    }

    private void LogRt950Rtx1Start(string information)
    {
        AppendSystemLine("*** RTX1 AUTO TEST START");
        AppendSystemLine($"*** FIRMWARE_EXPECTED={Rt950Rtx1Protocol.FirmwareExpected}");
        AppendSystemLine("*** SRC=TEST-1");
        AppendSystemLine("*** DST=APRS");
        AppendSystemLine("*** PATH=NONE");
        AppendSystemLine("*** CTRL=03");
        AppendSystemLine("*** PID=F0");
        AppendSystemLine($"*** INFO={information}");
    }

    private void LogRt950ProtocolFailure(string reason, bool handshakePass, bool fcsPass)
    {
        Rtx1TestState = Rt950Rtx1TestState.Failed;
        AppendSystemLine("*** PROTOCOL_ERROR");
        AppendSystemLine($"*** REASON={reason}");
        AppendSystemLine("*** RTX1_SEND=SKIPPED");
        LogRt950Rtx1Final(handshakePass, fcsPass, chunksPass: false, diagnosticSeen: false, transportPass: false);
    }

    private void LogRt950Rtx1Final(
        bool handshakePass,
        bool fcsPass,
        bool chunksPass,
        bool diagnosticSeen,
        bool transportPass)
    {
        AppendSystemLine("*** RTX1 AUTO TEST COMPLETE");
        AppendSystemLine($"*** BLE_HANDSHAKE={(handshakePass ? "PASS" : "FAIL")}");
        AppendSystemLine($"*** AX25_FCS={(fcsPass ? "PASS" : "NOT_COMPLETED")}");
        AppendSystemLine($"*** RTX1_ALL_CHUNKS={(chunksPass ? "PASS" : "FAIL")}");
        AppendSystemLine($"*** BLE_TRANSPORT={(transportPass ? "PASS" : "FAIL")}");
        AppendSystemLine($"*** RTX1_DIAGNOSTIC_NOTIFICATION={(diagnosticSeen ? "SEEN_UNPARSED" : "NOT_SEEN")}");
        AppendSystemLine("*** RTX1_DEVICE_ACK=NOT_SEEN");
        AppendSystemLine("*** RF_TX=UNKNOWN");
    }
}
