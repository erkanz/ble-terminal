using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace BLESerialTerminal;

public partial class DecodedPacketsWindow : Window
{
    private readonly ObservableCollection<DecodedPacketRow> _packets;

    internal DecodedPacketsWindow(ObservableCollection<DecodedPacketRow> packets)
    {
        InitializeComponent();
        _packets = packets;
        PacketGrid.ItemsSource = _packets;
        _packets.CollectionChanged += Packets_CollectionChanged;
        Closed += DecodedPacketsWindow_Closed;
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        UpdateCount();

        if (_packets.Count > 0)
        {
            PacketGrid.SelectedItem = _packets[^1];
            PacketGrid.ScrollIntoView(_packets[^1]);
        }
    }

    private void DecodedPacketsWindow_Closed(object? sender, EventArgs e)
    {
        _packets.CollectionChanged -= Packets_CollectionChanged;
    }

    private void Packets_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Packets_CollectionChanged(sender, e));
            return;
        }

        UpdateCount();
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 })
        {
            object newest = e.NewItems[e.NewItems.Count - 1]!;
            PacketGrid.ScrollIntoView(newest);
        }
    }

    private void UpdateCount() => CountTextBlock.Text = $"Decoded packets: {_packets.Count}";

    private void PacketGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        DetailTextBox.Text = PacketGrid.SelectedItem is DecodedPacketRow row ? row.DetailText : string.Empty;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _packets.Clear();
        DetailTextBox.Clear();
    }

    private void CopyDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (PacketGrid.SelectedItem is not DecodedPacketRow row)
            return;

        try
        {
            Clipboard.SetText(row.DetailText);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Clipboard error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export decoded KISS / AX.25 / APRS packets",
            Filter = "Text log (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"BLESerialTerminal-decoded-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("BLE Serial Terminal - Decoded KISS / AX.25 / APRS packets");
        sb.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Packet count: {_packets.Count}");
        sb.AppendLine();
        sb.AppendLine("Timestamp\tBLE\tKISS\tSource\tDestination\tPath\tType\tSummary");
        foreach (DecodedPacketRow row in _packets)
            sb.AppendLine(row.ExportLine);

        sb.AppendLine();
        sb.AppendLine("======================================================================");
        sb.AppendLine("PACKET DETAILS");
        sb.AppendLine("======================================================================");
        foreach (DecodedPacketRow row in _packets)
        {
            sb.AppendLine();
            sb.AppendLine(row.DetailText);
            sb.AppendLine("----------------------------------------------------------------------");
        }

        System.IO.File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            bool dark = Application.Current.Resources["WindowBackgroundBrush"] is System.Windows.Media.SolidColorBrush brush &&
                        (brush.Color.R + brush.Color.G + brush.Color.B) < 384;
            int enabled = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        }
        catch
        {
            // Theme hint is cosmetic only.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
