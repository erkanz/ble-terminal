using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BLESerialTerminal;

internal sealed class Rt950ToolsWindow : Window
{
    private readonly MainWindow _host;
    private readonly DispatcherTimer _timer;
    private readonly CheckBox _enable = new() { Content = "Enable RT950 tools" };
    private readonly CheckBox _autoUnlock = new() { Content = "Auto unlock on connect" };
    private readonly TextBlock _bleState = new();
    private readonly TextBlock _usbState = new();
    private readonly TextBlock _routeState = new();
    private readonly TextBox _repeat = new() { Text = "1", Width = 52, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly Button _preset = NewButton("Apply RT950 GATT Preset", 175);
    private readonly Button _unlock = NewButton("Unlock RT950 BLE", 145);
    private readonly Button _rtx1Ble = NewButton("Run RTX1 TX Test — BLE", 185);
    private readonly Button _rtx1Usb = NewButton("Run RTX1 TX Test — USB", 185);
    private readonly Button _diagnostics = NewButton("Snapshot Diagnostics", 145);
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

    internal Rt950ToolsWindow(MainWindow host)
    {
        _host = host;
        Title = "RT950 Tools — BLE Serial Terminal";
        Width = 820;
        Height = 610;
        MinWidth = 700;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");
        Content = BuildContent();

        _enable.Checked += async (_, _) => await SetEnabledAsync(true);
        _enable.Unchecked += async (_, _) => await SetEnabledAsync(false);
        _autoUnlock.Checked += async (_, _) => await SetAutoUnlockAsync(true);
        _autoUnlock.Unchecked += async (_, _) => await SetAutoUnlockAsync(false);
        _preset.Click += async (_, _) => await RunActionAsync(_host.ApplyRt950PresetFromWindowAsync);
        _unlock.Click += async (_, _) => await RunActionAsync(_host.UnlockRt950FromWindowAsync);
        _rtx1Ble.Click += async (_, _) => await RunCountedActionAsync(_host.RunRt950Rtx1BleFromWindowAsync);
        _rtx1Usb.Click += async (_, _) => await RunCountedActionAsync(_host.RunRt950Rtx1UsbFromWindowAsync);
        _diagnostics.Click += (_, _) => _host.EmitRt950DiagnosticsFromWindow();

        _host.Rt950ToolActivity += Host_Rt950ToolActivity;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _host.Rt950ToolActivity -= Host_Rt950ToolActivity;
        };
        RefreshState();
    }

    private UIElement BuildContent()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var root = new StackPanel { Margin = new Thickness(14) };
        scroll.Content = root;

        var title = new TextBlock
        {
            Text = "RT950 device-specific diagnostics",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        root.Children.Add(title);
        var subtitle = new TextBlock
        {
            Text = "This window owns RT950-specific presets, unlock and RTX1 tests. Closing it does not change the generic BLE terminal connection.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundBrush");
        root.Children.Add(subtitle);

        root.Children.Add(Card(BuildConnectionPanel()));
        root.Children.Add(Card(BuildActionPanel()));

        var activityHeader = new TextBlock
        {
            Text = "RT950 tool activity",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 6)
        };
        root.Children.Add(activityHeader);
        root.Children.Add(_activity);
        return scroll;
    }

    private UIElement BuildConnectionPanel()
    {
        var panel = new StackPanel();
        var toggles = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _enable.Margin = new Thickness(0, 0, 18, 0);
        toggles.Children.Add(_enable);
        toggles.Children.Add(_autoUnlock);
        panel.Children.Add(toggles);

        _bleState.Margin = new Thickness(0, 2, 0, 2);
        _usbState.Margin = new Thickness(0, 2, 0, 2);
        _routeState.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(_bleState);
        panel.Children.Add(_usbState);
        panel.Children.Add(_routeState);
        return panel;
    }

    private UIElement BuildActionPanel()
    {
        var panel = new StackPanel();
        var legacyLabel = new TextBlock
        {
            Text = "RT950 BLE setup / diagnostics",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
        panel.Children.Add(legacyLabel);

        var setup = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        setup.Children.Add(_preset);
        setup.Children.Add(_unlock);
        setup.Children.Add(_diagnostics);
        panel.Children.Add(setup);

        var rtxLabel = new TextBlock
        {
            Text = "RTX1 transmit diagnostic",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
        panel.Children.Add(rtxLabel);

        var rtx = new WrapPanel();
        rtx.Children.Add(new TextBlock
        {
            Text = "Repeat:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        _repeat.Margin = new Thickness(0, 0, 10, 0);
        rtx.Children.Add(_repeat);
        rtx.Children.Add(_rtx1Ble);
        rtx.Children.Add(_rtx1Usb);
        panel.Children.Add(rtx);

        var note = new TextBlock
        {
            Text = "BLE uses the RT950 FF31/FFE1 diagnostic route. USB writes the verified RTX1 binary packet to the connected COM stream. Transport success never proves RF TX.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundBrush");
        panel.Children.Add(note);
        return panel;
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

    private async Task SetEnabledAsync(bool enabled)
    {
        if (_syncing)
            return;
        await _host.SetRt950ToolsEnabledFromWindowAsync(enabled);
        RefreshState();
    }

    private async Task SetAutoUnlockAsync(bool enabled)
    {
        if (_syncing)
            return;
        await _host.SetRt950AutoUnlockFromWindowAsync(enabled);
        RefreshState();
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        SetActionButtons(false);
        try
        {
            await action();
        }
        finally
        {
            RefreshState();
        }
    }

    private async Task RunCountedActionAsync(Func<int, Task> action)
    {
        if (!int.TryParse(_repeat.Text.Trim(), out int count) || count is < 1 or > 99)
        {
            MessageBox.Show(this, "Repeat must be from 1 to 99.", "RT950 Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetActionButtons(false);
        try
        {
            await action(count);
        }
        finally
        {
            RefreshState();
        }
    }

    private void RefreshState()
    {
        Rt950ToolSnapshot s = _host.GetRt950ToolSnapshot();
        _syncing = true;
        try
        {
            _enable.IsChecked = s.Enabled;
            _autoUnlock.IsChecked = s.AutoUnlock;
        }
        finally
        {
            _syncing = false;
        }

        _bleState.Text = s.BleConnected ? "BLE: CONNECTED" : "BLE: DISCONNECTED";
        _usbState.Text = s.UsbConnected
            ? $"USB Serial: CONNECTED {s.UsbPort} @ {s.UsbBaud}"
            : "USB Serial: DISCONNECTED";
        _routeState.Text = $"Service={s.Service}  Notify={s.Notify}  Write={s.Write}  FF31={(s.Ff31Found ? "YES" : "NO")}  FFE1 Notify={(s.Ffe1NotifyActive ? "ACTIVE" : "NOT READY")}  Unlock={s.UnlockState}";

        _autoUnlock.IsEnabled = s.Enabled;
        _preset.IsEnabled = s.Enabled && s.BleConnected && !s.TestInProgress;
        _unlock.IsEnabled = s.Enabled && s.BleConnected && !s.TestInProgress;
        _diagnostics.IsEnabled = s.Enabled;
        _rtx1Ble.IsEnabled = s.Enabled && s.BleConnected && !s.TestInProgress;
        _rtx1Usb.IsEnabled = s.Enabled && s.UsbConnected && !s.TestInProgress;
    }

    private void SetActionButtons(bool enabled)
    {
        _preset.IsEnabled = enabled;
        _unlock.IsEnabled = enabled;
        _diagnostics.IsEnabled = enabled;
        _rtx1Ble.IsEnabled = enabled;
        _rtx1Usb.IsEnabled = enabled;
    }

    private void Host_Rt950ToolActivity(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => Host_Rt950ToolActivity(line));
            return;
        }
        _activity.AppendText(line + Environment.NewLine);
        _activity.ScrollToEnd();
    }
}
