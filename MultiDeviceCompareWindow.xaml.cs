using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Windows.Devices.Bluetooth.Advertisement;

namespace BLESerialTerminal;

public partial class MultiDeviceCompareWindow : Window
{
    private const int MaxSessions = 4;
    private readonly ObservableCollection<MultiDeviceDiscoveryItem> _visibleDiscovery = new();
    private readonly Dictionary<ulong, MultiDeviceDiscoveryItem> _discoveryByAddress = new();
    private readonly ObservableCollection<MultiDeviceSessionRow> _sessionRows = new();
    private readonly Dictionary<string, MultiDeviceSession> _sessionsById = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _refreshTimer;
    private readonly Func<ulong, bool>? _addressReservedByMain;
    private BluetoothLEAdvertisementWatcher? _watcher;
    private bool _closing;

    public MultiDeviceCompareWindow(Func<ulong, bool>? addressReservedByMain = null)
    {
        _addressReservedByMain = addressReservedByMain;
        InitializeComponent();
        DiscoveryGrid.ItemsSource = _visibleDiscovery;
        SessionGrid.ItemsSource = _sessionRows;
        CompareAComboBox.ItemsSource = _sessionRows;
        CompareBComboBox.ItemsSource = _sessionRows;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
    }

    private void StartScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_watcher != null)
            return;

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.Received += Watcher_Received;
        _watcher.Stopped += Watcher_Stopped;
        _watcher.Start();
        StartScanButton.IsEnabled = false;
        StopScanButton.IsEnabled = true;
        AppendDiagnostic("SCAN STARTED");
    }

    private void StopScanButton_Click(object sender, RoutedEventArgs e) => StopScan();

    private void StopScan()
    {
        BluetoothLEAdvertisementWatcher? watcher = _watcher;
        _watcher = null;
        if (watcher != null)
        {
            try
            {
                watcher.Received -= Watcher_Received;
                watcher.Stopped -= Watcher_Stopped;
                watcher.Stop();
            }
            catch { }
        }
        StartScanButton.IsEnabled = !_closing;
        StopScanButton.IsEnabled = false;
    }

    private void Watcher_Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing)
            {
                StartScanButton.IsEnabled = true;
                StopScanButton.IsEnabled = false;
            }
            AppendDiagnostic($"SCAN STOPPED error={args.Error}");
        });
    }

    private void Watcher_Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        string name = args.Advertisement.LocalName;
        ulong address = args.BluetoothAddress;
        short rssi = args.RawSignalStrengthInDBm;
        DateTime now = DateTime.Now;

        Dispatcher.BeginInvoke(() =>
        {
            if (!_discoveryByAddress.TryGetValue(address, out MultiDeviceDiscoveryItem? item))
            {
                item = new MultiDeviceDiscoveryItem
                {
                    Address = address,
                    Name = string.IsNullOrWhiteSpace(name) ? "Unnamed BLE device" : name,
                    Rssi = rssi,
                    LastSeen = now
                };
                _discoveryByAddress[address] = item;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(name))
                    item.Name = name;
                item.Rssi = rssi;
                item.LastSeen = now;
            }
            RefreshDiscoveryFilter();
        });
    }

    private void ScanFilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshDiscoveryFilter();

    private void RefreshDiscoveryFilter()
    {
        string filter = (ScanFilterTextBox.Text ?? string.Empty).Trim();
        MultiDeviceDiscoveryItem? selected = DiscoveryGrid.SelectedItem as MultiDeviceDiscoveryItem;
        _visibleDiscovery.Clear();
        foreach (MultiDeviceDiscoveryItem item in _discoveryByAddress.Values
                     .Where(item => string.IsNullOrEmpty(filter) ||
                                    item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                    item.AddressText.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(item => item.Rssi)
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            _visibleDiscovery.Add(item);
        }
        if (selected != null)
            DiscoveryGrid.SelectedItem = _visibleDiscovery.FirstOrDefault(x => x.Address == selected.Address);
    }

    private async void ConnectSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (DiscoveryGrid.SelectedItem is not MultiDeviceDiscoveryItem item)
        {
            MessageBox.Show(this, "Select a discovered BLE device first.", "Multi-Device Compare", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_addressReservedByMain?.Invoke(item.Address) == true)
        {
            MessageBox.Show(this,
                "This BLE address is currently owned by the main terminal connection. Disconnect it there before opening an independent compare session for the same physical device.",
                "BLE address already in use",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        MultiDeviceSession? existing = _sessionsById.Values.FirstOrDefault(s => s.Address == item.Address);
        if (existing != null)
        {
            string state = existing.Snapshot.State;
            if (state is "DISCONNECTED" or "ERROR")
            {
                AppendDiagnostic($"[{existing.SessionId}] RECONNECT REQUEST {item.Name} {item.AddressText}");
                await existing.ConnectAsync();
                UpdateRow(existing);
                return;
            }

            MessageBox.Show(this,
                $"This BLE address already has an active compare session ({state}).",
                "Multi-Device Compare",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_sessionsById.Count >= MaxSessions)
        {
            MessageBox.Show(this, $"A maximum of {MaxSessions} independent live compare sessions is allowed.", "Multi-Device Compare", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var session = new MultiDeviceSession(item.Address, item.Name);
        session.Updated += Session_Updated;
        session.Diagnostic += Session_Diagnostic;
        _sessionsById[session.SessionId] = session;
        var row = new MultiDeviceSessionRow(session.Snapshot);
        _sessionRows.Add(row);
        EnsureCompareSelections();
        AppendDiagnostic($"[{session.SessionId}] CONNECT REQUEST {item.Name} {item.AddressText}");
        await session.ConnectAsync();
        UpdateRow(session);
    }

    private async void DisconnectSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (SessionGrid.SelectedItem is not MultiDeviceSessionRow row || !_sessionsById.TryGetValue(row.SessionId, out MultiDeviceSession? session))
            return;
        await session.DisconnectAsync();
        UpdateRow(session);
    }

    private async void DisconnectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (MultiDeviceSession session in _sessionsById.Values.ToArray())
        {
            try { await session.DisconnectAsync(); } catch { }
            UpdateRow(session);
        }
    }

    private async void SendSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (SessionGrid.SelectedItem is not MultiDeviceSessionRow row || !_sessionsById.TryGetValue(row.SessionId, out MultiDeviceSession? session))
        {
            MessageBox.Show(this, "Select one independent live session first.", "Multi-Device Compare", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        byte[] data;
        try
        {
            data = ParseHex(KissTxTextBox.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid KISS HEX", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        KissTcpBridgeBleWriteResult result = await session.WriteKissAsync(data);
        AppendDiagnostic($"[{session.SessionId}] TX RESULT success={result.Success} bytes={result.BytesWritten} {result.Message}");
        if (!result.Success)
            MessageBox.Show(this, result.Message, "Multi-device KISS TX failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        UpdateRow(session);
    }

    private void RefreshComparisonButton_Click(object sender, RoutedEventArgs e) => RefreshComparison();

    private void RefreshComparison()
    {
        if (CompareAComboBox.SelectedItem is not MultiDeviceSessionRow a || CompareBComboBox.SelectedItem is not MultiDeviceSessionRow b)
        {
            ComparisonGrid.ItemsSource = null;
            return;
        }
        ComparisonGrid.ItemsSource = MultiDeviceComparisonEngine.Compare(a.Snapshot, b.Snapshot);
    }

    private void EnsureCompareSelections()
    {
        if (CompareAComboBox.SelectedIndex < 0 && _sessionRows.Count > 0)
            CompareAComboBox.SelectedIndex = 0;
        if (CompareBComboBox.SelectedIndex < 0 && _sessionRows.Count > 1)
            CompareBComboBox.SelectedIndex = 1;
        RefreshComparison();
    }

    private void Session_Updated(MultiDeviceSession session) => Dispatcher.BeginInvoke(() => UpdateRow(session));

    private void Session_Diagnostic(MultiDeviceSession session, string text) =>
        Dispatcher.BeginInvoke(() => AppendDiagnostic($"[{session.SessionId}] {text}"));

    private void UpdateRow(MultiDeviceSession session)
    {
        MultiDeviceSessionRow? row = _sessionRows.FirstOrDefault(x => x.SessionId == session.SessionId);
        row?.Update(session.Snapshot);
        RefreshComparison();
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        foreach (MultiDeviceSession session in _sessionsById.Values)
            UpdateRow(session);
    }

    private void ClearDiagnosticsButton_Click(object sender, RoutedEventArgs e) => DiagnosticTextBox.Clear();

    private void AppendDiagnostic(string text)
    {
        DiagnosticTextBox.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}");
        if (DiagnosticTextBox.Text.Length > 200_000)
            DiagnosticTextBox.Text = DiagnosticTextBox.Text[^150_000..];
        DiagnosticTextBox.ScrollToEnd();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closed(object? sender, EventArgs e)
    {
        if (_closing)
            return;
        _closing = true;
        StopScan();
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;

        foreach (MultiDeviceSession session in _sessionsById.Values.ToArray())
        {
            session.Updated -= Session_Updated;
            session.Diagnostic -= Session_Diagnostic;
            try { await session.DisposeAsync(); } catch { }
        }
        _sessionsById.Clear();
        _sessionRows.Clear();
    }

    private static byte[] ParseHex(string? text)
    {
        string compact = new((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-' && c != ':').ToArray());
        if (compact.Length == 0)
            throw new InvalidDataException("HEX payload is empty.");
        if ((compact.Length & 1) != 0)
            throw new InvalidDataException("HEX payload must contain complete byte pairs.");

        byte[] data = new byte[compact.Length / 2];
        for (int i = 0; i < data.Length; i++)
        {
            if (!byte.TryParse(compact.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out data[i]))
                throw new InvalidDataException($"Invalid HEX byte near character {i * 2 + 1}.");
        }
        return data;
    }

    private sealed class MultiDeviceSessionRow : INotifyPropertyChanged
    {
        private MultiDeviceSessionSnapshot _snapshot;

        public MultiDeviceSessionRow(MultiDeviceSessionSnapshot snapshot) => _snapshot = snapshot;
        public event PropertyChangedEventHandler? PropertyChanged;
        public MultiDeviceSessionSnapshot Snapshot => _snapshot;
        public string SessionId => _snapshot.SessionId;
        public string DeviceName => _snapshot.DeviceName;
        public string Address => _snapshot.Address;
        public string State => _snapshot.State;
        public string Profile => _snapshot.Profile;
        public string ServiceUuid => _snapshot.ServiceUuid;
        public string WriteUuid => _snapshot.WriteUuid;
        public string NotifyUuid => _snapshot.NotifyUuid;
        public string CccdText => _snapshot.CccdReady ? "YES" : "NO";
        public long Notifications => _snapshot.Notifications;
        public long RxBytes => _snapshot.RxBytes;
        public long KissFrames => _snapshot.KissFrames;
        public long Ax25Frames => _snapshot.Ax25Frames;
        public long AprsPackets => _snapshot.AprsPackets;
        public long TxBytes => _snapshot.TxBytes;
        public string LastPacket => _snapshot.LastSummary == "-"
            ? (_snapshot.LastError.Length == 0 ? "-" : _snapshot.LastError)
            : $"{_snapshot.LastSource}>{_snapshot.LastDestination} {_snapshot.LastAprsType}: {_snapshot.LastSummary}";
        public string Display => $"{DeviceName} [{Address}] ({State})";

        public void Update(MultiDeviceSessionSnapshot snapshot)
        {
            _snapshot = snapshot;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }
}
