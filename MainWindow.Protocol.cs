using System.Collections.ObjectModel;
using System.Windows;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private const int MaxDecodedPacketHistory = 5000;
    private readonly ObservableCollection<DecodedPacketRow> _decodedPackets = new();
    private DecodedPacketsWindow? _decodedPacketsWindow;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        KissStreamDecoder.AnyFrameCompleted += KissStreamDecoder_AnyFrameCompleted;
        InitializeNotificationCaptureHooks();
        InitializeLogExplorerHooks();
        InitializeRt950TxDiagnostics();
        InitializeKissTcpBridge();
    }

    protected override void OnClosed(EventArgs e)
    {
        KissStreamDecoder.AnyFrameCompleted -= KissStreamDecoder_AnyFrameCompleted;
        ShutdownKissTcpBridge();
        ShutdownRt950TxDiagnostics();
        ShutdownNotificationCaptureHooks();
        ShutdownLogExplorerHooks();
        try
        {
            _decodedPacketsWindow?.Close();
        }
        catch
        {
        }
        _decodedPacketsWindow = null;
        CloseNotificationMonitorWindow();
        CloseSessionCaptureWindow();
        base.OnClosed(e);
    }

    private void DecodedPacketsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_decodedPacketsWindow is { IsLoaded: true })
        {
            _decodedPacketsWindow.Activate();
            return;
        }

        _decodedPacketsWindow = new DecodedPacketsWindow(_decodedPackets)
        {
            Owner = this
        };
        _decodedPacketsWindow.Closed += (_, _) => _decodedPacketsWindow = null;
        _decodedPacketsWindow.Show();
    }

    private void KissStreamDecoder_AnyFrameCompleted(KissStreamDecoder decoder, KissFrame frame)
    {
        // Inspector and future transports may own independent KISS decoders. Only consume
        // frames produced by the main terminal connection's decoder here.
        if (!ReferenceEquals(decoder, _autoKissDecoder))
            return;

        string sourceCharacteristic = _notifyCharacteristic != null
            ? BleUuid.Short(_notifyCharacteristic.Uuid)
            : "UNKNOWN";
        DateTime timestamp = DateTime.Now;

        // Push() is called while the main RX dispatcher callback is still logging KISS.
        // Queue protocol decode so RAW BLE and KISS processing remains ordered, but keep
        // AX.25/APRS decode text out of the serial terminal. The dedicated decoded window
        // and structured diagnostic log own higher-layer decode presentation.
        Dispatcher.BeginInvoke(() => ProcessDecodedKissFrame(timestamp, sourceCharacteristic, frame));
    }

    private void ProcessDecodedKissFrame(DateTime timestamp, string sourceCharacteristic, KissFrame frame)
    {
        DecodedPacketRow row = DecodedPacketRow.Decode(timestamp, sourceCharacteristic, frame);
        _decodedPackets.Add(row);
        while (_decodedPackets.Count > MaxDecodedPacketHistory)
            _decodedPackets.RemoveAt(0);

        string device = CurrentLogDevice();
        if (row.Ax25 != null)
        {
            string path = string.IsNullOrWhiteSpace(row.Ax25.Path) ? "-" : row.Ax25.Path;
            string pid = row.Ax25.Pid.HasValue ? row.Ax25.Pid.Value.ToString("X2") : "--";
            _structuredLogStore.Add(
                LogCategory.AX25,
                $"AX25 RX SRC={row.Ax25.Source.Display} DST={row.Ax25.Destination.Display} PATH={path} TYPE={row.Ax25.FrameType} PID={pid}",
                direction: "RX",
                device: device,
                characteristic: sourceCharacteristic,
                timestamp: timestamp,
                data: frame.Data);
        }
        else if (!string.IsNullOrWhiteSpace(row.DecodeError))
        {
            _structuredLogStore.Add(
                LogCategory.AX25,
                $"AX25 DECODE SKIPPED/FAILED: {row.DecodeError}",
                direction: "RX",
                device: device,
                characteristic: sourceCharacteristic,
                isWarning: true,
                timestamp: timestamp,
                data: frame.Data);
        }

        if (row.Aprs != null)
        {
            _structuredLogStore.Add(
                LogCategory.APRS,
                $"APRS RX TYPE={row.Aprs.Category} SUMMARY={row.Aprs.Summary}",
                direction: "RX",
                device: device,
                characteristic: sourceCharacteristic,
                timestamp: timestamp,
                data: row.Ax25?.Information ?? Array.Empty<byte>());
        }
    }
}
