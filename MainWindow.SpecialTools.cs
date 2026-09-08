using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private bool _genericMainUiPrepared;
    private MenuItem? _specialToolsMenu;
    private Rt950ToolsWindow? _rt950ToolsWindow;
    private KissToolsWindow? _kissToolsWindow;

    internal event Action<string>? Rt950ToolActivity;
    internal event Action<string>? KissToolActivity;

    internal void PrepareGenericMainUi()
    {
        // This method intentionally runs after the generic USB Serial panel is created.
        // It is idempotent because App.OnActivated can be raised more than once.
        RemoveSpecialControlsFromMainSurface();
        InstallSpecialToolLauncherMenu();
        InstallGenericAboutMenu();

        if (_genericMainUiPrepared)
            return;

        _genericMainUiPrepared = true;
        GattInspectorButton.IsEnabledChanged += GenericMainConnectionPresentationChanged;
        NormalizeGenericMainConnectionPresentation();
    }

    private void RemoveSpecialControlsFromMainSurface()
    {
        // Legacy static RT950 controls are kept in XAML only for compatibility with older
        // code-behind handlers, but they must never be visible in the generic terminal UI.
        foreach (string label in new[]
                 {
                     "RT950 OEM TEST",
                     "RT950 FFE1 TEST",
                     "Load RT950 Test Commands",
                     "Run RTX1 TX Test"
                 })
        {
            RemoveFromParent(FindButtonByContent(this, label));
        }

        // Persisted RT950 command presets would otherwise leak device-specific labels back
        // into the generic command workspace.
        RemoveLegacyRt950CommandRowsFromNormalWorkspace();

        // USB Serial is a generic transport and remains on the main screen. RTX1 is not
        // generic, so its button and explanatory note live only in RT950 Tools.
        if (_usbSerialRtx1Button != null)
        {
            RemoveFromParent(_usbSerialRtx1Button);
            _usbSerialRtx1Button = null;
        }
        RemoveTextBlocksContaining(this, "USB RTX1 sends the verified RTX1 binary packet");

        if (Content is not DockPanel dock)
            return;
        Menu? menu = dock.Children.OfType<Menu>().FirstOrDefault();
        if (menu == null)
            return;

        if (_rt950Menu != null)
            menu.Items.Remove(_rt950Menu);
        if (_kissMenu != null)
            menu.Items.Remove(_kissMenu);

        MenuItem? view = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => HeaderText(item).Equals("View", StringComparison.OrdinalIgnoreCase));
        if (view != null)
        {
            RemoveMenuItemByHeader(view, "Decoded KISS / AX.25 / APRS packets...");
            RemoveMenuItemByHeader(view, "KISS TCP Bridge...");
            CleanupMenuSeparators(view);
        }
    }

    private static void RemoveFromParent(FrameworkElement? element)
    {
        if (element?.Parent is Panel panel)
            panel.Children.Remove(element);
        else if (element?.Parent is Decorator decorator && ReferenceEquals(decorator.Child, element))
            decorator.Child = null;
        else if (element?.Parent is ContentControl content && ReferenceEquals(content.Content, element))
            content.Content = null;
    }

    private static void RemoveTextBlocksContaining(DependencyObject root, string fragment)
    {
        var matches = new List<TextBlock>();
        CollectTextBlocksContaining(root, fragment, matches);
        foreach (TextBlock text in matches)
            RemoveFromParent(text);
    }

    private static void CollectTextBlocksContaining(DependencyObject root, string fragment, List<TextBlock> matches)
    {
        if (root is TextBlock text && text.Text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            matches.Add(text);

        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject dependencyObject)
                CollectTextBlocksContaining(dependencyObject, fragment, matches);
        }
    }

    private void InstallSpecialToolLauncherMenu()
    {
        if (Content is not DockPanel dock)
            return;
        Menu? menu = dock.Children.OfType<Menu>().FirstOrDefault();
        if (menu == null)
            return;

        MenuItem? existing = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => HeaderText(item).Equals("Tools", StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            _specialToolsMenu = existing;
            return;
        }

        _specialToolsMenu = new MenuItem { Header = "_Tools" };
        var rt950 = new MenuItem { Header = "RT950 Tools..." };
        rt950.Click += (_, _) => ShowRt950ToolsWindow();
        var kiss = new MenuItem { Header = "KISS Tools..." };
        kiss.Click += (_, _) => ShowKissToolsWindow();
        _specialToolsMenu.Items.Add(rt950);
        _specialToolsMenu.Items.Add(kiss);

        MenuItem? settings = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => HeaderText(item).Equals("Settings", StringComparison.OrdinalIgnoreCase));
        int index = settings != null ? menu.Items.IndexOf(settings) : Math.Max(0, menu.Items.Count - 1);
        menu.Items.Insert(index, _specialToolsMenu);
    }

    private void InstallGenericAboutMenu()
    {
        if (Content is not DockPanel dock)
            return;
        Menu? menu = dock.Children.OfType<Menu>().FirstOrDefault();
        MenuItem? about = menu?.Items.OfType<MenuItem>()
            .FirstOrDefault(item => HeaderText(item).Equals("About", StringComparison.OrdinalIgnoreCase));
        if (about == null || about.Tag?.ToString() == "GENERIC_ABOUT")
            return;

        about.Items.Clear();
        var item = new MenuItem { Header = "About BLE Serial Terminal" };
        item.Click += (_, _) => MessageBox.Show(
            this,
            "BLE Serial Terminal\n\nGeneric Windows BLE GATT / UART diagnostic terminal.\n" +
            "Core functions include BLE scanning, auto/custom GATT routing, raw RX/TX, notifications, logging, session capture, GATT inspection and USB serial.",
            "About BLE Serial Terminal",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        about.Items.Add(item);
        about.Tag = "GENERIC_ABOUT";
    }

    private static void RemoveMenuItemByHeader(MenuItem parent, string header)
    {
        MenuItem? match = parent.Items.OfType<MenuItem>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), header, StringComparison.OrdinalIgnoreCase));
        if (match != null)
            parent.Items.Remove(match);
    }

    private static void CleanupMenuSeparators(MenuItem parent)
    {
        for (int i = parent.Items.Count - 1; i >= 0; i--)
        {
            if (parent.Items[i] is not Separator)
                continue;
            bool atEdge = i == 0 || i == parent.Items.Count - 1;
            bool adjacentSeparator = !atEdge && parent.Items[i - 1] is Separator;
            if (atEdge || adjacentSeparator)
                parent.Items.RemoveAt(i);
        }
    }

    private void GenericMainConnectionPresentationChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        _ = Dispatcher.BeginInvoke(NormalizeGenericMainConnectionPresentation, DispatcherPriority.ContextIdle);

    private void NormalizeGenericMainConnectionPresentation()
    {
        if (!_bleConnected || !_autoGatt.IsAvailable)
            return;

        // Device-family recognition is not a mode selector for the generic terminal.
        // Once the generic route is established, the main UI presents only the transport.
        if (_autoGatt.IsRadtelRt950Kiss ||
            _autoGatt.ProfileName.Equals("RADTEL_RT950_KISS", StringComparison.OrdinalIgnoreCase))
        {
            _autoGatt.IsRadtelRt950Kiss = false;
            _autoGatt.ProfileName = BleUuid.Is(_autoGatt.Service?.Uuid ?? Guid.Empty, "FFE0") &&
                                    BleUuid.Is(_autoGatt.WriteCharacteristic?.Uuid ?? Guid.Empty, "FFE1")
                ? "Generic BLE-UART (FFE0/FFE1)"
                : "Generic BLE-UART";
            _radtelKissReady = false;
        }

        if (GattInspectorButton.IsEnabled)
            SetStatus($"BLE UART ready: {_autoGatt.ProfileName}");
    }

    private void ShowRt950ToolsWindow()
    {
        if (_rt950ToolsWindow is { IsLoaded: true })
        {
            _rt950ToolsWindow.Activate();
            return;
        }

        _rt950ToolsWindow = new Rt950ToolsWindow(this) { Owner = this };
        _rt950ToolsWindow.Closed += (_, _) => _rt950ToolsWindow = null;
        _rt950ToolsWindow.Show();
    }

    private void ShowKissToolsWindow()
    {
        if (_kissToolsWindow is { IsLoaded: true })
        {
            _kissToolsWindow.Activate();
            return;
        }

        _kissToolsWindow = new KissToolsWindow(this) { Owner = this };
        _kissToolsWindow.Closed += (_, _) => _kissToolsWindow = null;
        _kissToolsWindow.Show();
    }

    internal Rt950ToolSnapshot GetRt950ToolSnapshot()
    {
        bool ff31 = CurrentServiceCharacteristics().Any(c => BleUuid.Is(c.Uuid, "FF31"));
        return new Rt950ToolSnapshot(
            _optionalFeatureSettings.Rt950ToolsEnabled,
            _optionalFeatureSettings.Rt950AutoUnlockOnConnect,
            _bleConnected,
            _service == null ? "-" : BleUuid.Short(_service.Uuid),
            _notifyCharacteristic == null ? "-" : BleUuid.Short(_notifyCharacteristic.Uuid),
            _writeCharacteristic == null ? "-" : BleUuid.Short(_writeCharacteristic.Uuid),
            ff31,
            _ffe1CccdEnabled,
            _rt950Unlocked,
            _rt950UnlockState.ToString(),
            Volatile.Read(ref _rtx1TestInProgress) != 0,
            _usbSerial?.IsOpen == true,
            _usbSerial?.PortName ?? "-",
            _usbSerial?.BaudRate ?? 0);
    }

    internal async Task SetRt950ToolsEnabledFromWindowAsync(bool enabled)
    {
        if (_optionalFeatureSettings.Rt950ToolsEnabled == enabled)
            return;

        _optionalFeatureSettings.Rt950ToolsEnabled = enabled;
        if (_rt950EnableMenuItem != null)
            _rt950EnableMenuItem.IsChecked = enabled;

        if (!enabled)
        {
            _rt950UnlockResponseTcs?.TrySetCanceled();
            ResetRt950FeatureSession();
            EmitRt950Activity("RT950 tools disabled. Generic BLE connection is unchanged.");
        }
        else
        {
            EmitRt950Activity("RT950 tools enabled.");
            if (_bleConnected)
            {
                _rt950UnlockState = _servicesDiscovered ? Rt950UnlockState.ServicesDiscovered : Rt950UnlockState.Connected;
                if (_ffe1CccdEnabled && _notifyCharacteristic != null && BleUuid.Is(_notifyCharacteristic.Uuid, "FFE1"))
                    _rt950UnlockState = Rt950UnlockState.Ffe1NotifyReady;
                if (_optionalFeatureSettings.Rt950AutoUnlockOnConnect)
                    await QueueRt950UnlockAsync("AUTO_TOOL_WINDOW");
            }
        }

        SaveOptionalFeatureSettings();
        UpdateOptionalFeatureMenuState();
    }

    internal async Task SetRt950AutoUnlockFromWindowAsync(bool enabled)
    {
        _optionalFeatureSettings.Rt950AutoUnlockOnConnect = enabled;
        if (_rt950AutoUnlockMenuItem != null)
            _rt950AutoUnlockMenuItem.IsChecked = enabled;
        SaveOptionalFeatureSettings();
        UpdateOptionalFeatureMenuState();
        EmitRt950Activity($"Auto unlock {(enabled ? "enabled" : "disabled")}.");
        if (enabled && _optionalFeatureSettings.Rt950ToolsEnabled && _bleConnected)
            await QueueRt950UnlockAsync("AUTO_TOOL_WINDOW_ENABLED");
    }

    internal async Task ApplyRt950PresetFromWindowAsync()
    {
        EmitRt950Activity("Applying RT950 GATT preset...");
        await ApplyRt950GattPresetAsync();
        EmitRt950Activity("RT950 GATT preset action completed.");
    }

    internal async Task UnlockRt950FromWindowAsync()
    {
        EmitRt950Activity("Starting RT950 BLE unlock...");
        await QueueRt950UnlockAsync("TOOL_WINDOW");
        EmitRt950Activity($"RT950 unlock state: {_rt950UnlockState}.");
    }

    internal async Task RunRt950Rtx1BleFromWindowAsync(int count)
    {
        EmitRt950Activity($"Starting RTX1 BLE test x{count}...");
        await RunRt950Rtx1TestAsync(count);
        EmitRt950Activity("RTX1 BLE test completed. RF result remains independent of transport success.");
    }

    internal async Task RunRt950Rtx1UsbFromWindowAsync(int count)
    {
        EmitRt950Activity($"Starting RTX1 USB Serial test x{count}...");
        await RunRt950Rtx1UsbTestAsync(count);
        EmitRt950Activity("RTX1 USB Serial test completed. RF result remains independent of serial write success.");
    }

    internal void EmitRt950DiagnosticsFromWindow()
    {
        Rt950ToolSnapshot s = GetRt950ToolSnapshot();
        EmitRt950Activity($"BLE={YesNo(s.BleConnected)} SERVICE={s.Service} NOTIFY={s.Notify} WRITE={s.Write} FF31={YesNo(s.Ff31Found)} CCCD={YesNo(s.Ffe1NotifyActive)} UNLOCK={s.UnlockState} USB={YesNo(s.UsbConnected)}");
    }

    internal KissToolSnapshot GetKissToolSnapshot() => new(
        _optionalFeatureSettings.KissToolsEnabled,
        _optionalFeatureSettings.KissRxDecoderEnabled,
        _optionalFeatureSettings.KissShowRawFrames,
        _optionalFeatureSettings.KissShowDecoded,
        _bleConnected,
        _notifyCharacteristic == null ? "-" : BleUuid.Short(_notifyCharacteristic.Uuid),
        _decodedPackets.Count,
        _kissTcpBridge != null);

    internal void SetKissToolsEnabledFromWindow(bool enabled)
    {
        _optionalFeatureSettings.KissToolsEnabled = enabled;
        if (_kissEnableMenuItem != null)
            _kissEnableMenuItem.IsChecked = enabled;
        ClearKissFeatureState(log: false);
        SaveOptionalFeatureSettings();
        UpdateOptionalFeatureMenuState();
        EmitKissActivity($"KISS tools {(enabled ? "enabled" : "disabled")}.");
    }

    internal void SetKissDecoderFromWindow(bool enabled)
    {
        _optionalFeatureSettings.KissRxDecoderEnabled = enabled;
        if (_kissDecoderMenuItem != null)
            _kissDecoderMenuItem.IsChecked = enabled;
        ClearKissFeatureState(log: false);
        SaveOptionalFeatureSettings();
        UpdateOptionalFeatureMenuState();
        EmitKissActivity($"KISS RX decoder {(enabled ? "enabled" : "disabled")}.");
    }

    internal void SetKissShowRawFromWindow(bool enabled)
    {
        _optionalFeatureSettings.KissShowRawFrames = enabled;
        if (_kissRawMenuItem != null)
            _kissRawMenuItem.IsChecked = enabled;
        SaveOptionalFeatureSettings();
        UpdateOptionalFeatureMenuState();
        EmitKissActivity($"Raw KISS frame display {(enabled ? "enabled" : "disabled")}.");
    }

    internal void SetKissShowDecodedFromWindow(bool enabled)
    {
        _optionalFeatureSettings.KissShowDecoded = enabled;
        if (_kissDecodedMenuItem != null)
            _kissDecodedMenuItem.IsChecked = enabled;
        SaveOptionalFeatureSettings();
        UpdateOptionalFeatureMenuState();
        EmitKissActivity($"Decoded AX.25/APRS display {(enabled ? "enabled" : "disabled")}.");
    }

    internal void OpenDecodedPacketsFromKissWindow()
    {
        EmitKissActivity("Opening decoded AX.25/APRS packet window.");
        DecodedPacketsMenuItem_Click(this, new RoutedEventArgs());
    }

    internal void OpenKissFrameBuilderFromWindow()
    {
        EmitKissActivity("Opening KISS frame builder.");
        ShowKissFrameBuilder();
    }

    internal void OpenKissTcpBridgeFromWindow()
    {
        EmitKissActivity("Opening KISS TCP bridge.");
        KissTcpBridgeMenuItem_Click(this, new RoutedEventArgs());
    }

    internal void ClearKissStateFromWindow()
    {
        ClearKissFeatureState(log: false);
        EmitKissActivity("KISS decoder state cleared.");
    }

    private void EmitRt950Activity(string message) =>
        Rt950ToolActivity?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    private void EmitKissActivity(string message) =>
        KissToolActivity?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}

internal sealed record Rt950ToolSnapshot(
    bool Enabled,
    bool AutoUnlock,
    bool BleConnected,
    string Service,
    string Notify,
    string Write,
    bool Ff31Found,
    bool Ffe1NotifyActive,
    bool Unlocked,
    string UnlockState,
    bool TestInProgress,
    bool UsbConnected,
    string UsbPort,
    int UsbBaud);

internal sealed record KissToolSnapshot(
    bool Enabled,
    bool DecoderEnabled,
    bool ShowRaw,
    bool ShowDecoded,
    bool BleConnected,
    string Notify,
    int DecodedPacketCount,
    bool TcpBridgeAvailable);
