using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace BLESerialTerminal;

public partial class NotificationMonitorWindow : Window
{
    private const int MaxVisibleRows = 5000;
    private readonly NotificationCaptureStore _store;
    private readonly ObservableCollection<NotificationRecord> _visible = new();
    private bool _paused;

    internal NotificationMonitorWindow(NotificationCaptureStore store)
    {
        InitializeComponent();
        _store = store;
        NotificationGrid.ItemsSource = _visible;

        _store.RecordAdded += Store_RecordAdded;
        _store.Cleared += Store_Cleared;

        Loaded += NotificationMonitorWindow_Loaded;
        Closed += NotificationMonitorWindow_Closed;
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
    }

    private void NotificationMonitorWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RebuildVisibleFromStore();
        UpdateCounters();
    }

    private void NotificationMonitorWindow_Closed(object? sender, EventArgs e)
    {
        _store.RecordAdded -= Store_RecordAdded;
        _store.Cleared -= Store_Cleared;
    }

    private void Store_RecordAdded(NotificationRecord record)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_paused)
                {
                    _visible.Add(record);
                    while (_visible.Count > MaxVisibleRows)
                        _visible.RemoveAt(0);

                    if (AutoScrollCheckBox.IsChecked == true)
                        NotificationGrid.ScrollIntoView(record);
                }

                UpdateCounters();
            });
        }
        catch
        {
            // Window close races must never interfere with the BLE receive callback.
        }
    }

    private void Store_Cleared()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                _visible.Clear();
                DetailTextBox.Clear();
                UpdateCounters();
            });
        }
        catch
        {
            // Window close races are harmless.
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? "Resume display" : "Pause display";
        DisplayStateText.Text = _paused ? "PAUSED (capture continues)" : "LIVE";

        if (!_paused)
            RebuildVisibleFromStore();

        UpdateCounters();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => _store.Clear();

    private void CopyHexButton_Click(object sender, RoutedEventArgs e)
    {
        if (NotificationGrid.SelectedItem is NotificationRecord record)
            Clipboard.SetText(record.Hex);
    }

    private void CopyTextButton_Click(object sender, RoutedEventArgs e)
    {
        if (NotificationGrid.SelectedItem is NotificationRecord record)
            Clipboard.SetText(record.Ascii);
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export BLE notification capture",
            Filter = "Tab-separated text (*.tsv)|*.tsv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"BLE_Notifications_{DateTime.Now:yyyyMMdd_HHmmss}.tsv"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        IReadOnlyList<NotificationRecord> snapshot = _store.Snapshot();
        NotificationStatsSnapshot stats = _store.Stats();
        var export = new StringBuilder();
        export.AppendLine("BLE Serial Terminal - Notification Monitor Export");
        export.AppendLine($"Exported\t{DateTime.Now:O}");
        export.AppendLine($"Captured notifications\t{stats.Notifications}");
        export.AppendLine($"Captured bytes\t{stats.Bytes}");
        export.AppendLine();
        export.AppendLine("Sequence\tTimestamp\tDevice\tService\tCharacteristic\tSource\tReused\tMode\tLength\tKISS frames\tHEX\tASCII");

        foreach (NotificationRecord record in snapshot)
        {
            export.Append(record.Sequence).Append('\t')
                .Append(record.Timestamp.ToString("O")).Append('\t')
                .Append(CleanField(record.Device)).Append('\t')
                .Append(record.ServiceUuid?.ToString("D") ?? string.Empty).Append('\t')
                .Append(record.CharacteristicUuid.ToString("D")).Append('\t')
                .Append(CleanField(record.Source)).Append('\t')
                .Append(record.Reused).Append('\t')
                .Append(CleanField(record.DeliveryMode)).Append('\t')
                .Append(record.Length).Append('\t')
                .Append(record.KissFrameCount?.ToString() ?? string.Empty).Append('\t')
                .Append(record.Hex).Append('\t')
                .Append(CleanField(record.Ascii)).AppendLine();
        }

        System.IO.File.WriteAllText(dialog.FileName, export.ToString(), new UTF8Encoding(false));
    }

    private void NotificationGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NotificationGrid.SelectedItem is not NotificationRecord record)
        {
            DetailTextBox.Clear();
            return;
        }

        var detail = new StringBuilder();
        detail.AppendLine($"Sequence:       {record.Sequence}");
        detail.AppendLine($"Timestamp:      {record.Timestamp:O}");
        detail.AppendLine($"Device:         {record.Device}");
        detail.AppendLine($"Service:        {(record.ServiceUuid.HasValue ? BleUuid.Full(record.ServiceUuid.Value) : "unknown")}");
        detail.AppendLine($"Characteristic: {BleUuid.Full(record.CharacteristicUuid)}");
        detail.AppendLine($"Source:         {record.Source}");
        detail.AppendLine($"Reused:         {record.Reused}");
        detail.AppendLine($"Mode:           {record.DeliveryMode}");
        detail.AppendLine($"Length:         {record.Length}");
        detail.AppendLine($"KISS frames:    {record.KissText}");
        detail.AppendLine();
        detail.AppendLine("HEX:");
        detail.AppendLine(record.Hex);
        detail.AppendLine();
        detail.AppendLine("ASCII-safe:");
        detail.AppendLine(record.Ascii);
        DetailTextBox.Text = detail.ToString();
    }

    private void RebuildVisibleFromStore()
    {
        IReadOnlyList<NotificationRecord> snapshot = _store.Snapshot();
        _visible.Clear();
        int start = Math.Max(0, snapshot.Count - MaxVisibleRows);
        for (int i = start; i < snapshot.Count; i++)
            _visible.Add(snapshot[i]);

        if (_visible.Count > 0 && AutoScrollCheckBox.IsChecked == true)
            NotificationGrid.ScrollIntoView(_visible[^1]);
    }

    private void UpdateCounters()
    {
        NotificationStatsSnapshot stats = _store.Stats();
        string perCharacteristic = stats.ByCharacteristic.Count == 0
            ? "none"
            : string.Join("   ", stats.ByCharacteristic
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => $"{kvp.Key}: {kvp.Value.Notifications} / {kvp.Value.Bytes} B"));

        CountersText.Text =
            $"Captured {stats.Notifications} notifications / {stats.Bytes} bytes   |   Visible {_visible.Count}" +
            (_paused ? "   |   DISPLAY PAUSED; CAPTURE ACTIVE" : string.Empty) +
            $"   |   Per characteristic: {perCharacteristic}";
    }

    private static string CleanField(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private void ApplyTitleBarTheme()
    {
        try
        {
            bool dark = false;
            if (Application.Current.Resources["WindowBackgroundBrush"] is SolidColorBrush brush)
            {
                Color c = brush.Color;
                dark = (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 128;
            }

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            int enabled = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        }
        catch
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
