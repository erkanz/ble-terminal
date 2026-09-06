using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace BLESerialTerminal;

public partial class LogFilterWindow : Window
{
    private const int MaxVisibleRows = 20000;
    private readonly LogStore _store;
    private readonly Func<string> _metadataProvider;
    private readonly ObservableCollection<LogEntry> _visibleEntries = new();
    private bool _initializing = true;
    private bool _updatingFilters;
    private int _filteredResultCount;

    internal LogFilterWindow(LogStore store, Func<string> metadataProvider)
    {
        _store = store;
        _metadataProvider = metadataProvider;
        InitializeComponent();
        LogGrid.ItemsSource = _visibleEntries;
        _store.EntryAdded += Store_EntryAdded;
        _store.Cleared += Store_Cleared;
        Closed += LogFilterWindow_Closed;
        _initializing = false;
        RebuildView();
    }

    private void LogFilterWindow_Closed(object? sender, EventArgs e)
    {
        _store.EntryAdded -= Store_EntryAdded;
        _store.Cleared -= Store_Cleared;
    }

    private void FilterControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _updatingFilters)
            return;
        RebuildView();
    }

    private void FilterControl_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initializing || _updatingFilters)
            return;
        RebuildView();
    }

    private void FilterControl_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _updatingFilters)
            return;
        RebuildView();
    }

    private LogFilterCriteria CurrentCriteria() => new(
        SearchTextBox.Text ?? string.Empty,
        SelectedComboText(CategoryComboBox, "ALL"),
        DeviceFilterTextBox.Text ?? string.Empty,
        CharacteristicFilterTextBox.Text ?? string.Empty,
        SelectedComboText(DirectionComboBox, "ALL"),
        ErrorsWarningsOnlyCheckBox.IsChecked == true);

    private static string SelectedComboText(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item)
            return item.Content?.ToString() ?? fallback;
        return combo.SelectedItem?.ToString() ?? fallback;
    }

    private void RebuildView()
    {
        LogFilterCriteria criteria = CurrentCriteria();
        IReadOnlyList<LogEntry> filtered = LogFilterEngine.Filter(_store.Snapshot(), criteria);
        _filteredResultCount = filtered.Count;

        _visibleEntries.Clear();
        int start = Math.Max(0, filtered.Count - MaxVisibleRows);
        for (int i = start; i < filtered.Count; i++)
            _visibleEntries.Add(filtered[i]);

        UpdateResultCount();
        if (AutoScrollCheckBox.IsChecked == true && _visibleEntries.Count > 0)
        {
            LogEntry last = _visibleEntries[^1];
            LogGrid.ScrollIntoView(last);
        }
    }

    private void Store_EntryAdded(LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!LogFilterEngine.Matches(entry, CurrentCriteria()))
                return;

            _filteredResultCount++;
            _visibleEntries.Add(entry);
            while (_visibleEntries.Count > MaxVisibleRows)
                _visibleEntries.RemoveAt(0);
            UpdateResultCount();

            if (AutoScrollCheckBox.IsChecked == true)
                LogGrid.ScrollIntoView(entry);
        });
    }

    private void Store_Cleared()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _visibleEntries.Clear();
            _filteredResultCount = 0;
            DetailTextBox.Clear();
            UpdateResultCount();
        });
    }

    private void UpdateResultCount()
    {
        string suffix = _filteredResultCount > MaxVisibleRows
            ? $" (showing newest {MaxVisibleRows})"
            : string.Empty;
        ResultCountTextBlock.Text = $"{_filteredResultCount:N0} results{suffix}";
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        _updatingFilters = true;
        try
        {
            SearchTextBox.Clear();
            CategoryComboBox.SelectedIndex = 0;
            DeviceFilterTextBox.Clear();
            CharacteristicFilterTextBox.Clear();
            DirectionComboBox.SelectedIndex = 0;
            ErrorsWarningsOnlyCheckBox.IsChecked = false;
        }
        finally
        {
            _updatingFilters = false;
        }
        RebuildView();
    }

    private void PreviousMatchButton_Click(object sender, RoutedEventArgs e) => MoveSelection(-1);

    private void NextMatchButton_Click(object sender, RoutedEventArgs e) => MoveSelection(1);

    private void MoveSelection(int delta)
    {
        if (_visibleEntries.Count == 0)
            return;

        int index = LogGrid.SelectedIndex;
        if (index < 0)
            index = delta > 0 ? 0 : _visibleEntries.Count - 1;
        else
            index = (index + delta + _visibleEntries.Count) % _visibleEntries.Count;

        AutoScrollCheckBox.IsChecked = false;
        LogGrid.SelectedIndex = index;
        LogGrid.ScrollIntoView(_visibleEntries[index]);
        LogGrid.Focus();
    }

    private void LogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogGrid.SelectedItem is not LogEntry entry)
        {
            DetailTextBox.Clear();
            return;
        }

        DetailTextBox.Text =
            $"Sequence: {entry.Sequence}{Environment.NewLine}" +
            $"Timestamp: {entry.Timestamp:O}{Environment.NewLine}" +
            $"Category: {entry.CategoryText}{Environment.NewLine}" +
            $"Level: {entry.LevelText}{Environment.NewLine}" +
            $"Direction: {entry.Direction}{Environment.NewLine}" +
            $"Device: {entry.Device}{Environment.NewLine}" +
            $"Characteristic: {entry.Characteristic}{Environment.NewLine}" +
            $"Message: {entry.Message}";
    }

    private void CopyRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (LogGrid.SelectedItem is not LogEntry entry)
            return;
        Clipboard.SetText(ToTsv(entry));
    }

    private void ExportFilteredButton_Click(object sender, RoutedEventArgs e)
    {
        LogFilterCriteria criteria = CurrentCriteria();
        IReadOnlyList<LogEntry> entries = LogFilterEngine.Filter(_store.Snapshot(), criteria);
        ExportEntries(entries, filtered: true, criteria);
    }

    private void ExportFullButton_Click(object sender, RoutedEventArgs e) =>
        ExportEntries(_store.Snapshot(), filtered: false, LogFilterCriteria.Empty);

    private void ExportEntries(IReadOnlyList<LogEntry> entries, bool filtered, LogFilterCriteria criteria)
    {
        var dialog = new SaveFileDialog
        {
            Title = filtered ? "Export filtered diagnostic log" : "Export full diagnostic log",
            Filter = "Text files (*.txt)|*.txt|Log files (*.log)|*.log|All files (*.*)|*.*",
            FileName = $"BLESerial_{(filtered ? "filtered" : "full")}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("BLE Serial Terminal - Structured Diagnostic Log Export");
        sb.AppendLine($"Exported: {DateTime.Now:O}");
        sb.AppendLine($"Mode: {(filtered ? "FILTERED" : "FULL")}");
        sb.AppendLine($"Entries: {entries.Count}");
        if (filtered)
        {
            sb.AppendLine($"Search: {criteria.Text}");
            sb.AppendLine($"Category: {criteria.Category}");
            sb.AppendLine($"Device filter: {criteria.Device}");
            sb.AppendLine($"Characteristic filter: {criteria.Characteristic}");
            sb.AppendLine($"Direction: {criteria.Direction}");
            sb.AppendLine($"Errors/warnings only: {criteria.ErrorsWarningsOnly}");
        }
        sb.AppendLine();
        sb.AppendLine("===== SESSION METADATA =====");
        sb.AppendLine(_metadataProvider());
        sb.AppendLine();
        sb.AppendLine("===== LOG =====");
        sb.AppendLine("Sequence\tTimestamp\tCategory\tLevel\tDirection\tDevice\tCharacteristic\tMessage");
        foreach (LogEntry entry in entries)
            sb.AppendLine(ToTsv(entry));

        System.IO.File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
    }

    private static string ToTsv(LogEntry entry) => string.Join('\t', new[]
    {
        entry.Sequence.ToString(),
        entry.Timestamp.ToString("O"),
        entry.CategoryText,
        entry.LevelText,
        entry.Direction,
        SanitizeTsv(entry.Device),
        SanitizeTsv(entry.Characteristic),
        SanitizeTsv(entry.Message)
    });

    private static string SanitizeTsv(string value) =>
        (value ?? string.Empty).Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
