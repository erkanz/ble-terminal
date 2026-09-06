using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BLESerialTerminal;

public partial class KissTcpBridgeWindow : Window
{
    private const int MaxEventRows = 2000;
    private readonly KissTcpBridgeServer _server;
    private readonly ObservableCollection<KissTcpBridgeLogRow> _events = new();
    private readonly DispatcherTimer _statsTimer;
    private bool _closing;

    internal KissTcpBridgeWindow(KissTcpBridgeServer server)
    {
        InitializeComponent();
        _server = server ?? throw new ArgumentNullException(nameof(server));
        EventGrid.ItemsSource = _events;
        _server.EventOccurred += Server_EventOccurred;

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _statsTimer.Tick += StatsTimer_Tick;
        _statsTimer.Start();
        UpdateStats();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_server.IsRunning)
            return;

        if (!int.TryParse(PortTextBox.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "TCP port must be from 1 to 65535.", "KISS TCP Bridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool lan = BindComboBox.SelectedIndex == 1;
        if (lan)
        {
            MessageBoxResult confirm = MessageBox.Show(
                this,
                "LAN / all-interfaces binding exposes the KISS TCP endpoint to other hosts allowed by Windows Firewall.\n\nContinue and listen on 0.0.0.0?",
                "Enable LAN KISS TCP binding",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        StartButton.IsEnabled = false;
        BindComboBox.IsEnabled = false;
        PortTextBox.IsEnabled = false;
        try
        {
            IPAddress address = lan ? IPAddress.Any : IPAddress.Loopback;
            await _server.StartAsync(new KissTcpBridgeOptions(address, port));
            StopButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not start KISS TCP bridge", MessageBoxButton.OK, MessageBoxImage.Error);
            StartButton.IsEnabled = true;
            BindComboBox.IsEnabled = true;
            PortTextBox.IsEnabled = true;
        }
        finally
        {
            UpdateStats();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        await _server.StopAsync();
        if (!_closing)
        {
            StartButton.IsEnabled = true;
            BindComboBox.IsEnabled = true;
            PortTextBox.IsEnabled = true;
        }
        UpdateStats();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => _events.Clear();

    private void StatsTimer_Tick(object? sender, EventArgs e) => UpdateStats();

    private void UpdateStats()
    {
        KissTcpBridgeStats stats = _server.GetStats();
        StateTextBlock.Text = stats.Running
            ? (stats.BleAvailable ? "RUNNING / BLE READY" : "RUNNING / BLE SUSPENDED")
            : "STOPPED";
        EndpointTextBlock.Text = $"Endpoint: {stats.Endpoint}";
        BleTextBlock.Text = $"BLE: {(stats.BleAvailable ? "ready" : "unavailable")}";
        ClientsTextBlock.Text = $"Clients: {stats.Clients}";
        BackpressureTextBlock.Text = $"Backpressure disconnects: {stats.BackpressureDisconnects}";
        BleRxTextBlock.Text = $"BLE→TCP: {stats.BleRxBytes} B / {stats.BleRxFrames} frames";
        TcpOutTextBlock.Text = $"TCP sent: {stats.TcpTxBytes} B";
        TcpRxTextBlock.Text = $"TCP→BLE: {stats.TcpRxBytes} B / {stats.TcpRxFrames} frames";
        BleTxTextBlock.Text = $"BLE written: {stats.BleTxBytes} B / reject {stats.RejectedWrites}";

        StartButton.IsEnabled = !stats.Running && !_closing;
        StopButton.IsEnabled = stats.Running && !_closing;
        BindComboBox.IsEnabled = !stats.Running && !_closing;
        PortTextBox.IsEnabled = !stats.Running && !_closing;
    }

    private void Server_EventOccurred(KissTcpBridgeEvent evt)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _events.Add(new KissTcpBridgeLogRow(evt.Timestamp, evt.Kind.ToString().ToUpperInvariant(), evt.Message));
            while (_events.Count > MaxEventRows)
                _events.RemoveAt(0);
            if (_events.Count > 0)
                EventGrid.ScrollIntoView(_events[^1]);
            UpdateStats();
        });
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        if (_closing)
            return;
        _closing = true;
        _statsTimer.Stop();
        _statsTimer.Tick -= StatsTimer_Tick;
        _server.EventOccurred -= Server_EventOccurred;
        try { await _server.StopAsync(); } catch { }
    }

    private sealed record KissTcpBridgeLogRow(DateTime Timestamp, string Kind, string Message)
    {
        public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
    }
}
