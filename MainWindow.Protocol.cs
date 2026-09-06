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
    }

    protected override void OnClosed(EventArgs e)
    {
        KissStreamDecoder.AnyFrameCompleted -= KissStreamDecoder_AnyFrameCompleted;
        try
        {
            _decodedPacketsWindow?.Close();
        }
        catch
        {
        }
        _decodedPacketsWindow = null;
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
        // Queue protocol decode so RAW BLE and KISS logs remain visibly earlier than AX.25/APRS.
        Dispatcher.BeginInvoke(() => ProcessDecodedKissFrame(timestamp, sourceCharacteristic, frame));
    }

    private void ProcessDecodedKissFrame(DateTime timestamp, string sourceCharacteristic, KissFrame frame)
    {
        DecodedPacketRow row = DecodedPacketRow.Decode(timestamp, sourceCharacteristic, frame);
        _decodedPackets.Add(row);
        while (_decodedPackets.Count > MaxDecodedPacketHistory)
            _decodedPackets.RemoveAt(0);

        if (row.Ax25 != null)
        {
            string path = string.IsNullOrWhiteSpace(row.Ax25.Path) ? "-" : row.Ax25.Path;
            string pid = row.Ax25.Pid.HasValue ? row.Ax25.Pid.Value.ToString("X2") : "--";
            AppendSystemLine($"AX25 RX SRC={row.Ax25.Source.Display} DST={row.Ax25.Destination.Display} PATH={path} TYPE={row.Ax25.FrameType} PID={pid}");
        }
        else if (!string.IsNullOrWhiteSpace(row.DecodeError))
        {
            AppendSystemLine($"AX25 DECODE SKIPPED/FAILED: {row.DecodeError}");
        }

        if (row.Aprs != null)
            AppendSystemLine($"APRS RX TYPE={row.Aprs.Category} SUMMARY={row.Aprs.Summary}");
    }
}
