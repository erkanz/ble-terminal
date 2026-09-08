using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BLESerialTerminal;

internal sealed class KissToolsWindow : Window
{
    private readonly MainWindow _host;
    private readonly DispatcherTimer _timer;
    private readonly CheckBox _enable = new() { Content = "Enable KISS tools" };
    private readonly CheckBox _decoder = new() { Content = "Enable KISS RX decoder" };
    private readonly CheckBox _showRaw = new() { Content = "Show raw KISS frames" };
    private readonly CheckBox _showDecoded = new() { Content = "Show decoded AX.25 / APRS" };
    private readonly TextBlock _state = new();
    private readonly TextBlock _counts = new();
    private readonly Button _decodedPackets = NewButton("Decoded AX.25 / APRS...", 175);
    private readonly Button _builder = NewButton("KISS Frame Builder...", 155);
    private readonly Button _tcpBridge = NewButton("KISS TCP Bridge...", 145);
    private readonly Button _clear = NewButton("Clear KISS State", 130);
    private readonly TextBox _activity = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        FontSize = 12,
        MinHeight = 190
    };
    private bool _syncing;

    internal KissToolsWindow(MainWindow host)
    {
        _host = host;
        Title = "KISS Tools — BLE Serial Terminal";
        Width = 760;
        Height = 560;
        MinWidth = 650;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");
        Content = BuildContent();

        _enable.Checked += (_, _) => SetEnabled(true);
        _enable.Unchecked += (_, _) => SetEnabled(false);
        _decoder.Checked += (_, _) => SetDecoder(true);
        _decoder.Unchecked += (_, _) => SetDecoder(false);
        _showRaw.Checked += (_, _) => SetShowRaw(true);
        _showRaw.Unchecked += (_, _) => SetShowRaw(false);
        _showDecoded.Checked += (_, _) => SetShowDecoded(true);
        _showDecoded.Unchecked += (_, _) => SetShowDecoded(false);
        _decodedPackets.Click += (_, _) => _host.OpenDecodedPacketsFromKissWindow();
        _builder.Click += (_, _) => _host.OpenKissFrameBuilderFromWindow();
        _tcpBridge.Click += (_, _) => _host.OpenKissTcpBridgeFromWindow();
        _clear.Click += (_, _) => _host.ClearKissStateFromWindow();

        _host.KissToolActivity += Host_KissToolActivity;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _host.KissToolActivity -= Host_KissToolActivity;
        };
        RefreshState();
    }

    private UIElement BuildContent()
    {
        var root = new StackPanel { Margin = new Thickness(14) };

        root.Children.Add(new TextBlock
        {
            Text = "KISS protocol tools",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        var subtitle = new TextBlock
        {
            Text = "KISS framing, AX.25/APRS decoding and TCP bridging are optional protocol tools. The main window remains a generic BLE diagnostic terminal.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundBrush");
        root.Children.Add(subtitle);

        var options = new StackPanel();
        _enable.Margin = new Thickness(0, 0, 0, 6);
        _decoder.Margin = new Thickness(0, 0, 0, 6);
        _showRaw.Margin = new Thickness(0, 0, 0, 6);
        _showDecoded.Margin = new Thickness(0, 0, 0, 8);
        options.Children.Add(_enable);
        options.Children.Add(_decoder);
        options.Children.Add(_showRaw);
        options.Children.Add(_showDecoded);
        options.Children.Add(_state);
        options.Children.Add(_counts);
        root.Children.Add(Card(options));

        var actions = new WrapPanel();
        actions.Children.Add(_decodedPackets);
        actions.Children.Add(_builder);
        actions.Children.Add(_tcpBridge);
        actions.Children.Add(_clear);
        root.Children.Add(Card(actions));

        root.Children.Add(new TextBlock
        {
            Text = "KISS tool activity",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 6)
        });
        root.Children.Add(_activity);
        return root;
    }

    private static Border Card(UIElement child)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 10),
            Child = child
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBackgroundBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }

    private static Button NewButton(string content, double width) => new()
    {
        Content = content,
        Width = width,
        Height = 30,
        Margin = new Thickness(0, 0, 8, 6)
    };

    private void SetEnabled(bool enabled)
    {
        if (_syncing)
            return;
        _host.SetKissToolsEnabledFromWindow(enabled);
        RefreshState();
    }

    private void SetDecoder(bool enabled)
    {
        if (_syncing)
            return;
        _host.SetKissDecoderFromWindow(enabled);
        RefreshState();
    }

    private void SetShowRaw(bool enabled)
    {
        if (_syncing)
            return;
        _host.SetKissShowRawFromWindow(enabled);
        RefreshState();
    }

    private void SetShowDecoded(bool enabled)
    {
        if (_syncing)
            return;
        _host.SetKissShowDecodedFromWindow(enabled);
        RefreshState();
    }

    private void RefreshState()
    {
        KissToolSnapshot s = _host.GetKissToolSnapshot();
        _syncing = true;
        try
        {
            _enable.IsChecked = s.Enabled;
            _decoder.IsChecked = s.DecoderEnabled;
            _showRaw.IsChecked = s.ShowRaw;
            _showDecoded.IsChecked = s.ShowDecoded;
        }
        finally
        {
            _syncing = false;
        }

        _state.Text = $"BLE={(s.BleConnected ? "CONNECTED" : "DISCONNECTED")}  Notify={s.Notify}";
        _counts.Text = $"Decoded packet history={s.DecodedPacketCount}  TCP bridge backend={(s.TcpBridgeAvailable ? "READY" : "UNAVAILABLE")}";

        _decoder.IsEnabled = s.Enabled;
        _showRaw.IsEnabled = s.Enabled && s.DecoderEnabled;
        _showDecoded.IsEnabled = s.Enabled && s.DecoderEnabled;
        _decodedPackets.IsEnabled = s.Enabled;
        _builder.IsEnabled = s.Enabled;
        _tcpBridge.IsEnabled = s.Enabled && s.TcpBridgeAvailable;
        _clear.IsEnabled = s.Enabled;
    }

    private void Host_KissToolActivity(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => Host_KissToolActivity(line));
            return;
        }
        _activity.AppendText(line + Environment.NewLine);
        _activity.ScrollToEnd();
    }
}
