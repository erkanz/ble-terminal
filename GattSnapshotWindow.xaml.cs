using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;

namespace BLESerialTerminal;

public partial class GattSnapshotWindow : Window
{
    private readonly Func<Task<GattSnapshot>> _captureCurrent;
    private readonly ObservableCollection<GattSnapshotDiffRow> _diffRows = new();
    private GattSnapshot? _snapshotA;
    private GattSnapshot? _snapshotB;
    private bool _busy;

    internal GattSnapshotWindow(Func<Task<GattSnapshot>> captureCurrent)
    {
        InitializeComponent();
        _captureCurrent = captureCurrent ?? throw new ArgumentNullException(nameof(captureCurrent));
        DiffGrid.ItemsSource = _diffRows;
    }

    private async void CaptureAButton_Click(object sender, RoutedEventArgs e) => await CaptureIntoAsync(isA: true);
    private async void CaptureBButton_Click(object sender, RoutedEventArgs e) => await CaptureIntoAsync(isA: false);

    private async Task CaptureIntoAsync(bool isA)
    {
        if (_busy) return;
        _busy = true;
        SetCaptureButtons(false);
        try
        {
            StatusText.Text = "Refreshing GATT Inspector discovery and capturing snapshot...";
            GattSnapshot snapshot = await _captureCurrent();
            if (isA)
                _snapshotA = snapshot;
            else
                _snapshotB = snapshot;
            UpdateSnapshotLabels();
            CompareIfReady();
            StatusText.Text = $"Snapshot {(isA ? "A" : "B")} captured successfully.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Capture failed: {ex.Message}";
            MessageBox.Show(this, ex.Message, "GATT snapshot capture failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            SetCaptureButtons(true);
        }
    }

    private void LoadAButton_Click(object sender, RoutedEventArgs e) => LoadSnapshot(isA: true);
    private void LoadBButton_Click(object sender, RoutedEventArgs e) => LoadSnapshot(isA: false);

    private void LoadSnapshot(bool isA)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Load GATT snapshot {(isA ? "A" : "B")}",
            Filter = "BLE GATT snapshot (*.gatt-snapshot.json)|*.gatt-snapshot.json|JSON files (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            string json = System.IO.File.ReadAllText(dialog.FileName, Encoding.UTF8);
            GattSnapshot snapshot = GattSnapshotSerializer.FromJson(json);
            if (isA)
                _snapshotA = snapshot;
            else
                _snapshotB = snapshot;
            UpdateSnapshotLabels();
            CompareIfReady();
            StatusText.Text = $"Loaded snapshot {(isA ? "A" : "B")}: {System.IO.Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "GATT snapshot load failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveAJsonButton_Click(object sender, RoutedEventArgs e) => SaveSnapshotJson(_snapshotA, "A");
    private void SaveBJsonButton_Click(object sender, RoutedEventArgs e) => SaveSnapshotJson(_snapshotB, "B");
    private void SaveATextButton_Click(object sender, RoutedEventArgs e) => SaveSnapshotText(_snapshotA, "A");
    private void SaveBTextButton_Click(object sender, RoutedEventArgs e) => SaveSnapshotText(_snapshotB, "B");

    private void SaveSnapshotJson(GattSnapshot? snapshot, string label)
    {
        if (snapshot == null)
        {
            MessageBox.Show(this, $"Snapshot {label} is empty.", "GATT snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = $"Save GATT snapshot {label}",
            FileName = DefaultSnapshotFileName(snapshot),
            Filter = "BLE GATT snapshot (*.gatt-snapshot.json)|*.gatt-snapshot.json|JSON files (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        System.IO.File.WriteAllText(dialog.FileName, GattSnapshotSerializer.ToJson(snapshot), new UTF8Encoding(false));
        StatusText.Text = $"Saved structured snapshot {label}: {dialog.FileName}";
    }

    private void SaveSnapshotText(GattSnapshot? snapshot, string label)
    {
        if (snapshot == null)
        {
            MessageBox.Show(this, $"Snapshot {label} is empty.", "GATT snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = $"Export GATT snapshot {label} as text",
            FileName = System.IO.Path.ChangeExtension(DefaultSnapshotFileName(snapshot), ".txt"),
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        System.IO.File.WriteAllText(dialog.FileName, GattSnapshotSerializer.ToText(snapshot), new UTF8Encoding(false));
        StatusText.Text = $"Saved text snapshot {label}: {dialog.FileName}";
    }

    private void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshotA == null || _snapshotB == null)
        {
            MessageBox.Show(this, "Capture or load both Snapshot A and Snapshot B first.", "GATT compare", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        CompareNow();
    }

    private void SwapButton_Click(object sender, RoutedEventArgs e)
    {
        (_snapshotA, _snapshotB) = (_snapshotB, _snapshotA);
        UpdateSnapshotLabels();
        CompareIfReady();
    }

    private void ExportDiffButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshotA == null || _snapshotB == null)
        {
            MessageBox.Show(this, "Capture or load both snapshots before exporting a diff.", "GATT compare", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_diffRows.Count == 0)
            CompareNow();

        var dialog = new SaveFileDialog
        {
            Title = "Export GATT snapshot diff",
            FileName = $"gatt-diff-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Text files (*.txt)|*.txt|TSV files (*.tsv)|*.tsv|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("BLE SERIAL TERMINAL - GATT SNAPSHOT DIFF");
        sb.AppendLine($"A: {SnapshotSummary(_snapshotA)}");
        sb.AppendLine($"B: {SnapshotSummary(_snapshotB)}");
        sb.AppendLine($"Changes: {_diffRows.Count}");
        sb.AppendLine();
        sb.AppendLine("CHANGE\tSCOPE\tPATH\tFIELD\tBEFORE\tAFTER");
        foreach (GattSnapshotDiffRow row in _diffRows)
            sb.AppendLine(string.Join("\t", Clean(row.Change), Clean(row.Scope), Clean(row.Path), Clean(row.Field), Clean(row.Before), Clean(row.After)));
        System.IO.File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
        StatusText.Text = $"Diff exported: {dialog.FileName}";
    }

    private void CompareIfReady()
    {
        if (_snapshotA != null && _snapshotB != null)
            CompareNow();
        else
        {
            _diffRows.Clear();
            SummaryText.Text = "Load or capture two snapshots.";
        }
    }

    private void CompareNow()
    {
        if (_snapshotA == null || _snapshotB == null)
            return;

        List<GattSnapshotDiffRow> rows = GattSnapshotComparer.Compare(_snapshotA, _snapshotB);
        _diffRows.Clear();
        foreach (GattSnapshotDiffRow row in rows)
            _diffRows.Add(row);

        int added = rows.Count(r => r.Change == "ADDED");
        int removed = rows.Count(r => r.Change == "REMOVED");
        int changed = rows.Count(r => r.Change == "CHANGED");
        SummaryText.Text = rows.Count == 0
            ? "IDENTICAL — no deterministic GATT differences found."
            : $"{rows.Count} differences: {added} added, {removed} removed, {changed} changed";
    }

    private void UpdateSnapshotLabels()
    {
        SnapshotAText.Text = _snapshotA == null ? "Not loaded" : SnapshotSummary(_snapshotA);
        SnapshotBText.Text = _snapshotB == null ? "Not loaded" : SnapshotSummary(_snapshotB);
    }

    private static string SnapshotSummary(GattSnapshot snapshot)
    {
        GattSnapshotMetadata m = snapshot.Metadata;
        int characteristics = snapshot.Services.Sum(s => s.Characteristics.Count);
        return $"{m.DeviceName} [{m.Address}]  {snapshot.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  services={snapshot.Services.Count} chars={characteristics}  profile={m.AutoProfile}";
    }

    private static string DefaultSnapshotFileName(GattSnapshot snapshot)
    {
        string device = string.IsNullOrWhiteSpace(snapshot.Metadata.DeviceName) ? "device" : snapshot.Metadata.DeviceName;
        foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
            device = device.Replace(invalid, '_');
        return $"{device}-{snapshot.CapturedAtUtc.ToLocalTime():yyyyMMdd-HHmmss}.gatt-snapshot.json";
    }

    private static string Clean(string value) => (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private void SetCaptureButtons(bool enabled)
    {
        CaptureAButton.IsEnabled = enabled;
        CaptureBButton.IsEnabled = enabled;
    }
}
