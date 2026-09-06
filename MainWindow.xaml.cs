using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BLESerialTerminal;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<BleDeviceRow> _allDevices = new();
    private readonly ObservableCollection<BleDeviceRow> _visibleDevices = new();
    private readonly Dictionary<ulong, BleDeviceRow> _deviceMap = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _gattOperationGate = new(1, 1);
    private AutoDetectedGattContext _autoGatt = new();

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _writeCharacteristic;
    private GattCharacteristic? _notifyCharacteristic;
    private GattInspectorWindow? _gattInspectorWindow;

    private ulong? _connectedAddress;
    private bool _manualDisconnect;
    private int _reconnectInProgress;
    private long _rxBytes;
    private long _txBytes;
    private DateTime? _connectedAt;
    private readonly StringBuilder _rxAsciiLineBuffer = new();
    private bool _pendingCr;
    private string? _terminalBackgroundOverride;
    private string? _terminalForegroundOverride;
    private GattCharacteristic? _notifyHandlerCharacteristic;
    private KissStreamDecoder _autoKissDecoder = new();
    private bool _bleConnected;
    private bool _servicesDiscovered;
    private bool _ffe1Found;
    private bool _ffe1HandlerAttached;
    private bool _ffe1CccdEnabled;
    private bool _radtelKissReady;

    private static string SettingsDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BLESerialTerminal");

    private static string ThemeSettingsFile => System.IO.Path.Combine(SettingsDirectory, "theme.txt");
    private static string TerminalColorSettingsFile => System.IO.Path.Combine(SettingsDirectory, "terminal-colors.txt");

    public MainWindow()
    {
        InitializeComponent();
        DeviceGrid.ItemsSource = _visibleDevices;

        LoadTerminalColorPreferences();
        bool darkMode = LoadDarkModePreference();
        DarkModeMenuItem.IsChecked = darkMode;
        ApplyTheme(darkMode);

        SourceInitialized += (_, _) => ApplyDarkTitleBar(DarkModeMenuItem.IsChecked);
        Closing += MainWindow_Closing;
    }

    private void DarkModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        bool darkMode = DarkModeMenuItem.IsChecked;
        ApplyTheme(darkMode);
        SaveDarkModePreference(darkMode);
        SetStatus(darkMode ? "Dark mode enabled" : "Light mode enabled");
    }

    private void ApplyTheme(bool darkMode)
    {
        ResourceDictionary resources = Application.Current.Resources;

        if (darkMode)
        {
            SetBrush(resources, "WindowBackgroundBrush", "#0D1117");
            SetBrush(resources, "PanelBackgroundBrush", "#131A24");
            SetBrush(resources, "ControlBackgroundBrush", "#0F1621");
            SetBrush(resources, "ControlHoverBrush", "#1C2736");
            SetBrush(resources, "ForegroundBrush", "#E6EDF3");
            SetBrush(resources, "MutedForegroundBrush", "#8B949E");
            SetBrush(resources, "BorderBrush", "#30363D");
            SetBrush(resources, "AccentBrush", "#4CC9F0");
            SetBrush(resources, "SelectionBrush", "#1F6FEB");
            SetBrush(resources, "SelectionForegroundBrush", "#FFFFFF");
            SetBrush(resources, "TerminalBackgroundBrush", "#060A0F");
            SetBrush(resources, "TerminalForegroundBrush", "#D7F9FF");
            SetBrush(resources, "GridAlternateBrush", "#101720");
            SetBrush(resources, "DisabledForegroundBrush", "#5F6B7A");
            SetBrush(resources, "ScrollTrackBrush", "#0A1018");
            SetBrush(resources, "ScrollThumbBrush", "#3B4654");
            SetBrush(resources, "ScrollThumbHoverBrush", "#667487");
        }
        else
        {
            SetBrush(resources, "WindowBackgroundBrush", "#F4F6F8");
            SetBrush(resources, "PanelBackgroundBrush", "#FFFFFF");
            SetBrush(resources, "ControlBackgroundBrush", "#FFFFFF");
            SetBrush(resources, "ControlHoverBrush", "#E9EEF5");
            SetBrush(resources, "ForegroundBrush", "#17202A");
            SetBrush(resources, "MutedForegroundBrush", "#667085");
            SetBrush(resources, "BorderBrush", "#CBD5E1");
            SetBrush(resources, "AccentBrush", "#2563EB");
            SetBrush(resources, "SelectionBrush", "#D8E8FF");
            SetBrush(resources, "SelectionForegroundBrush", "#0F172A");
            SetBrush(resources, "TerminalBackgroundBrush", "#111111");
            SetBrush(resources, "TerminalForegroundBrush", "#E8E8E8");
            SetBrush(resources, "GridAlternateBrush", "#F8FAFC");
            SetBrush(resources, "DisabledForegroundBrush", "#98A2B3");
            SetBrush(resources, "ScrollTrackBrush", "#EEF2F6");
            SetBrush(resources, "ScrollThumbBrush", "#AAB4C0");
            SetBrush(resources, "ScrollThumbHoverBrush", "#7C8998");
        }

        ApplyTerminalColorOverrides(resources);
        ApplyDarkTitleBar(darkMode);
    }

    private void ApplyTerminalColorOverrides(ResourceDictionary resources)
    {
        if (!string.IsNullOrWhiteSpace(_terminalBackgroundOverride))
            SetBrush(resources, "TerminalBackgroundBrush", _terminalBackgroundOverride);

        if (!string.IsNullOrWhiteSpace(_terminalForegroundOverride))
            SetBrush(resources, "TerminalForegroundBrush", _terminalForegroundOverride);
    }

    private static void SetBrush(ResourceDictionary resources, string key, string color)
    {
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static bool LoadDarkModePreference()
    {
        try
        {
            if (!System.IO.File.Exists(ThemeSettingsFile))
                return true;

            string value = System.IO.File.ReadAllText(ThemeSettingsFile).Trim();
            return !value.Equals("light", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static void SaveDarkModePreference(bool darkMode)
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(ThemeSettingsFile);
            if (!string.IsNullOrWhiteSpace(directory))
                System.IO.Directory.CreateDirectory(directory);

            System.IO.File.WriteAllText(ThemeSettingsFile, darkMode ? "dark" : "light");
        }
        catch
        {
            // Theme persistence must never interfere with BLE operation.
        }
    }

    private void LoadTerminalColorPreferences()
    {
        _terminalBackgroundOverride = null;
        _terminalForegroundOverride = null;

        try
        {
            if (!System.IO.File.Exists(TerminalColorSettingsFile))
                return;

            foreach (string line in System.IO.File.ReadAllLines(TerminalColorSettingsFile))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;

                string key = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();
                if (!TryNormalizeColor(value, out string normalized))
                    continue;

                if (key.Equals("background", StringComparison.OrdinalIgnoreCase))
                    _terminalBackgroundOverride = normalized;
                else if (key.Equals("foreground", StringComparison.OrdinalIgnoreCase))
                    _terminalForegroundOverride = normalized;
            }
        }
        catch
        {
            // User color preferences must never interfere with BLE operation.
        }
    }

    private void SaveTerminalColorPreferences()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsDirectory);
            string content = $"background={_terminalBackgroundOverride ?? string.Empty}{Environment.NewLine}" +
                             $"foreground={_terminalForegroundOverride ?? string.Empty}{Environment.NewLine}";
            System.IO.File.WriteAllText(TerminalColorSettingsFile, content, new UTF8Encoding(false));
        }
        catch
        {
            // User color preferences must never interfere with BLE operation.
        }
    }

    private static bool TryNormalizeColor(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string candidate = value.Trim();
        if (!candidate.StartsWith('#'))
            candidate = "#" + candidate;

        if (candidate.Length != 7 && candidate.Length != 9)
            return false;

        try
        {
            Color color = (Color)ColorConverter.ConvertFromString(candidate);
            normalized = color.A == 255
                ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GetCurrentBrushHex(string resourceKey)
    {
        if (Application.Current.Resources[resourceKey] is SolidColorBrush brush)
        {
            Color color = brush.Color;
            return color.A == 255
                ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        return "#000000";
    }

    private void TerminalBackgroundMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPickerWindow("Serial output background", GetCurrentBrushHex("TerminalBackgroundBrush"))
        {
            Owner = this
        };

        if (picker.ShowDialog() != true)
            return;

        _terminalBackgroundOverride = picker.SelectedColorHex;
        SetBrush(Application.Current.Resources, "TerminalBackgroundBrush", picker.SelectedColorHex);
        SaveTerminalColorPreferences();
        SetStatus($"Serial output background: {picker.SelectedColorHex}");
    }

    private void TerminalForegroundMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPickerWindow("Serial output text color", GetCurrentBrushHex("TerminalForegroundBrush"))
        {
            Owner = this
        };

        if (picker.ShowDialog() != true)
            return;

        _terminalForegroundOverride = picker.SelectedColorHex;
        SetBrush(Application.Current.Resources, "TerminalForegroundBrush", picker.SelectedColorHex);
        SaveTerminalColorPreferences();
        SetStatus($"Serial output text color: {picker.SelectedColorHex}");
    }

    private void ResetTerminalColorsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _terminalBackgroundOverride = null;
        _terminalForegroundOverride = null;
        SaveTerminalColorPreferences();
        ApplyTheme(DarkModeMenuItem.IsChecked);
        SetStatus("Serial output colors reset to theme defaults");
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "BLE Serial Terminal\n\nWindows BLE GATT / UART terminal and diagnostic tool.\n" +
            "Includes Auto Detect BLE-UART, GATT Inspector, multi-notify debugging and KISS frame detection.",
            "About BLE Serial Terminal",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ApplyDarkTitleBar(bool darkMode)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int enabled = darkMode ? 1 : 0;
            // DWMWA_USE_IMMERSIVE_DARK_MODE is 20 on current Windows 10/11 builds.
            // Attribute 19 is retained as a fallback for older Windows 10 builds.
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        }
        catch
        {
            // The application theme still works even if DWM rejects the title-bar hint.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _manualDisconnect = true;
        StopScan();
        CleanupConnection();
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_watcher is { Status: BluetoothLEAdvertisementWatcherStatus.Started })
        {
            StopScan();
            SetStatus("Scan stopped");
            return;
        }

        StartScan();
    }

    private void StartScan()
    {
        StopScan();
        _deviceMap.Clear();
        _allDevices.Clear();
        _visibleDevices.Clear();

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.Received += Watcher_Received;
        _watcher.Stopped += Watcher_Stopped;
        _watcher.Start();

        ScanButton.Content = "Stop scan";
        SetStatus("Scanning for BLE advertisements...");
    }

    private void StopScan()
    {
        if (_watcher == null) return;

        try
        {
            _watcher.Received -= Watcher_Received;
            _watcher.Stopped -= Watcher_Stopped;
            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                _watcher.Stop();
        }
        catch
        {
            // Ignore shutdown races from the Windows BLE stack.
        }
        finally
        {
            _watcher = null;
            if (IsLoaded) ScanButton.Content = "Scan";
        }
    }

    private void Watcher_Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ScanButton.Content = "Scan";
            if (args.Error != BluetoothError.Success)
                SetStatus($"BLE scan stopped: {args.Error}");
        });
    }

    private void Watcher_Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        string name = args.Advertisement.LocalName?.Trim() ?? string.Empty;
        int rssi = args.RawSignalStrengthInDBm;
        DateTime now = DateTime.Now;

        Dispatcher.BeginInvoke(() =>
        {
            if (_deviceMap.TryGetValue(args.BluetoothAddress, out BleDeviceRow? existing))
            {
                if (!string.IsNullOrWhiteSpace(name)) existing.Name = name;
                existing.Rssi = rssi;
                existing.LastSeen = now;
            }
            else
            {
                var row = new BleDeviceRow(args.BluetoothAddress, name, rssi, now);
                _deviceMap.Add(args.BluetoothAddress, row);
                _allDevices.Add(row);
            }

            RefreshDeviceFilter();
        });
    }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshDeviceFilter();

    private void RefreshDeviceFilter()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshDeviceFilter);
            return;
        }

        string filter = FilterTextBox.Text.Trim();
        var selectedAddress = (DeviceGrid.SelectedItem as BleDeviceRow)?.BluetoothAddress;

        _visibleDevices.Clear();
        foreach (var row in _allDevices
                     .Where(d => string.IsNullOrEmpty(filter) ||
                                 d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                 d.AddressText.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(d => d.Rssi))
        {
            _visibleDevices.Add(row);
        }

        if (selectedAddress.HasValue)
            DeviceGrid.SelectedItem = _visibleDevices.FirstOrDefault(d => d.BluetoothAddress == selectedAddress.Value);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceGrid.SelectedItem is not BleDeviceRow selected)
        {
            MessageBox.Show(this, "Select a BLE device first.", "BLE Serial Terminal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ConnectByAddressAsync(selected.BluetoothAddress, selected.Name, false);
    }

    private async void DeviceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceGrid.SelectedItem is BleDeviceRow selected)
            await ConnectByAddressAsync(selected.BluetoothAddress, selected.Name, false);
    }

    private void GattInspectorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_device == null || !_connectedAddress.HasValue)
        {
            MessageBox.Show(this, "Connect to a BLE device first.", "GATT Inspector", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_gattInspectorWindow != null && _gattInspectorWindow.IsLoaded)
        {
            _gattInspectorWindow.Activate();
            return;
        }

        string name = string.IsNullOrWhiteSpace(_device.Name) ? "Unnamed BLE device" : _device.Name;
        _gattInspectorWindow = new GattInspectorWindow(
            _device,
            name,
            _connectedAddress.Value,
            _connectedAt ?? DateTime.Now,
            _autoGatt.IsAvailable ? _autoGatt : null,
            _gattOperationGate)
        {
            Owner = this
        };
        _gattInspectorWindow.AutoTerminalCccdStateChanged += GattInspector_AutoTerminalCccdStateChanged;
        _gattInspectorWindow.Closed += (_, _) => _gattInspectorWindow = null;
        AppendSystemLine("GATT Inspector opened");
        _gattInspectorWindow.Show();
    }

    private void GattInspector_AutoTerminalCccdStateChanged(AutoDetectedGattContext context, bool enabled)
    {
        if (!ReferenceEquals(context, _autoGatt))
            return;

        _autoGatt.CccdEnabled = enabled;
        _ffe1CccdEnabled = _ffe1Found && enabled;
        _radtelKissReady = _autoGatt.IsRadtelRt950Kiss && enabled;
        AppendSystemLine($"INSPECTOR AUTO CCCD STATE={(enabled ? "ACTIVE" : "NOT_READY")}");
        if (_radtelKissReady)
        {
            AppendSystemLine("RADTEL KISS READY (Inspector subscription recovery)");
            SetStatus("RADTEL KISS READY");
        }
        LogConnectionStateSnapshot();
    }

    private async Task ConnectByAddressAsync(ulong address, string advertisedName, bool isReconnect)
    {
        await _connectGate.WaitAsync();
        try
        {
            _manualDisconnect = false;
            SetUiConnecting(true);
            StopScan();
            CleanupConnection();
            ResetConnectionState();

            bool autoDetect = AutoDetectGattCheckBox.IsChecked == true;
            Guid serviceUuid = Guid.Empty;
            Guid writeUuid = Guid.Empty;
            Guid notifyUuid = Guid.Empty;

            if (!TryReadChunkSize(out _))
                return;

            if (!autoDetect && !TryReadProfile(out serviceUuid, out writeUuid, out notifyUuid, out _))
                return;

            SetStatus(isReconnect ? "Reconnecting..." : (autoDetect ? "Connecting and auto-detecting BLE-UART profile..." : "Connecting..."));
            ConnectionTextBlock.Text = $"Connecting {FormatBluetoothAddress(address)}";

            BluetoothLEDevice? device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device == null)
                throw new InvalidOperationException("Windows could not open the BLE device. Make sure Bluetooth is enabled and the device is in range.");

            _device = device;
            _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;
            _bleConnected = true;
            AppendSystemLine($"BLE_CONNECTED device={FormatBluetoothAddress(address)} connectionState={_device.ConnectionStatus}");

            string profileName = autoDetect
                ? await AutoDetectGattProfileAsync(_device, advertisedName)
                : await OpenManualGattProfileAsync(_device, serviceUuid, writeUuid, notifyUuid);

            _servicesDiscovered = true;
            _connectedAddress = address;
            _connectedAt = DateTime.Now;

            if (_notifyCharacteristic == null)
                throw new InvalidOperationException("Receive characteristic is unavailable after GATT discovery.");

            _ffe1Found = BleUuid.Is(_notifyCharacteristic.Uuid, "FFE1");
            AttachNotifyHandler(_notifyCharacteristic);

            bool notificationReady = await EnableTerminalNotificationsAsync(_notifyCharacteristic);
            _ffe1CccdEnabled = _ffe1Found && notificationReady;
            _radtelKissReady = _autoGatt.IsRadtelRt950Kiss && notificationReady;
            if (_autoGatt.IsAvailable && ReferenceEquals(_autoGatt.NotifyCharacteristic, _notifyCharacteristic))
                _autoGatt.CccdEnabled = notificationReady;

            string name = string.IsNullOrWhiteSpace(_device.Name) ? advertisedName : _device.Name;
            if (string.IsNullOrWhiteSpace(name)) name = "Unnamed BLE device";

            ConnectionTextBlock.Text = $"Connected: {name} ({FormatBluetoothAddress(address)})";
            AppendSystemLine($"CONNECTED {name} [{FormatBluetoothAddress(address)}] profile={profileName}");
            LogConnectionStateSnapshot();
            SetUiConnected(true);

            if (notificationReady)
            {
                SetStatus(_radtelKissReady ? "RADTEL KISS READY" : $"BLE UART ready: {profileName}");
                if (_radtelKissReady)
                    AppendSystemLine("RADTEL KISS READY");
            }
            else if (_autoGatt.IsRadtelRt950Kiss)
            {
                SetStatus("BLE connected; RT950 KISS NOT READY (CCCD failed). Open GATT Inspector for diagnostics.");
                AppendSystemLine("RADTEL KISS NOT READY");
            }
            else
            {
                throw new InvalidOperationException("BLE connection established but notification/indication subscription failed.");
            }
        }
        catch (Exception ex)
        {
            AppendSystemLine($"CONNECT ERROR: {ex.Message}");
            SetStatus("Connection failed");
            CleanupConnection();
            SetUiConnected(false);

            if (!isReconnect)
                MessageBox.Show(this, ex.Message, "BLE connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetUiConnecting(false);
            _connectGate.Release();
        }
    }

    private async Task<string> OpenManualGattProfileAsync(BluetoothLEDevice device, Guid serviceUuid, Guid writeUuid, Guid notifyUuid)
    {
        await _gattOperationGate.WaitAsync();
        try
        {
            var serviceResult = await device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached);
            if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
                throw new InvalidOperationException($"Service {serviceUuid} not found. GATT status: {serviceResult.Status}.");

            _service = serviceResult.Services[0];
            for (int i = 1; i < serviceResult.Services.Count; i++)
            {
                try { serviceResult.Services[i].Dispose(); } catch { }
            }

            var writeResult = await _service.GetCharacteristicsForUuidAsync(writeUuid, BluetoothCacheMode.Uncached);
            if (writeResult.Status != GattCommunicationStatus.Success || writeResult.Characteristics.Count == 0)
                throw new InvalidOperationException($"Write characteristic {writeUuid} not found. GATT status: {writeResult.Status}.");
            _writeCharacteristic = writeResult.Characteristics[0];

            var notifyResult = await _service.GetCharacteristicsForUuidAsync(notifyUuid, BluetoothCacheMode.Uncached);
            if (notifyResult.Status != GattCommunicationStatus.Success || notifyResult.Characteristics.Count == 0)
                throw new InvalidOperationException($"Notify characteristic {notifyUuid} not found. GATT status: {notifyResult.Status}.");
            _notifyCharacteristic = notifyResult.Characteristics[0];

            return "Custom GATT profile";
        }
        finally
        {
            _gattOperationGate.Release();
        }
    }

    private async Task<string> AutoDetectGattProfileAsync(BluetoothLEDevice device, string advertisedName)
    {
        await _gattOperationGate.WaitAsync();
        try
        {
            var servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                throw new InvalidOperationException($"Could not enumerate GATT services. GATT status: {servicesResult.Status}.");

            GattDeviceService? bestService = null;
            GattCharacteristic? bestWrite = null;
            GattCharacteristic? bestNotify = null;
            List<GattCharacteristic>? bestServiceCharacteristics = null;
            int bestScore = int.MinValue;
            string bestProfileName = string.Empty;

            foreach (GattDeviceService service in servicesResult.Services)
            {
                if (IsBluetoothSigStandardService(service.Uuid))
                {
                    try { service.Dispose(); } catch { }
                    continue;
                }

                GattCharacteristicsResult charsResult;
                try
                {
                    charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                }
                catch
                {
                    try { service.Dispose(); } catch { }
                    continue;
                }

                if (charsResult.Status != GattCommunicationStatus.Success)
                {
                    try { service.Dispose(); } catch { }
                    continue;
                }

                var writeCandidates = charsResult.Characteristics
                    .Where(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse) ||
                                c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write))
                    .ToList();

                var notifyCandidates = charsResult.Characteristics
                    .Where(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) ||
                                c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                    .ToList();

                if (writeCandidates.Count == 0 || notifyCandidates.Count == 0)
                {
                    try { service.Dispose(); } catch { }
                    continue;
                }

                foreach (GattCharacteristic write in writeCandidates)
                {
                    foreach (GattCharacteristic notify in notifyCandidates)
                    {
                        int score = ScoreUartCandidate(service.Uuid, write, notify, out string profileName);
                        if (score <= bestScore)
                            continue;

                        if (bestService != null && !ReferenceEquals(bestService, service))
                        {
                            try { bestService.Dispose(); } catch { }
                        }

                        bestScore = score;
                        bestService = service;
                        bestWrite = write;
                        bestNotify = notify;
                        bestServiceCharacteristics = charsResult.Characteristics.ToList();
                        bestProfileName = profileName;
                    }
                }

                if (!ReferenceEquals(bestService, service))
                {
                    try { service.Dispose(); } catch { }
                }
            }

            if (bestService == null || bestWrite == null || bestNotify == null)
                throw new InvalidOperationException("No BLE-UART style GATT profile was found. The device has no suitable Write + Notify/Indicate pair. Use Custom mode for a proprietary profile.");

            _service = bestService;
            _writeCharacteristic = bestWrite;
            _notifyCharacteristic = bestNotify;

            bool ffe0Ffe1Shared = BleUuid.Is(bestService.Uuid, "FFE0") &&
                                  BleUuid.Is(bestWrite.Uuid, "FFE1") &&
                                  BleUuid.Is(bestNotify.Uuid, "FFE1") &&
                                  bestNotify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify);
            bool isRadtel = ffe0Ffe1Shared && (IsRadtelDeviceName(device.Name) || IsRadtelDeviceName(advertisedName));
            if (isRadtel)
                bestProfileName = "RADTEL_RT950_KISS";

            _autoGatt.Service = bestService;
            _autoGatt.WriteCharacteristic = bestWrite;
            _autoGatt.NotifyCharacteristic = bestNotify;
            _autoGatt.ServiceCharacteristics.Clear();
            if (bestServiceCharacteristics != null)
                _autoGatt.ServiceCharacteristics.AddRange(bestServiceCharacteristics);
            _autoGatt.ProfileName = bestProfileName;
            _autoGatt.IsRadtelRt950Kiss = isRadtel;
            _autoGatt.NotifyHandlerAttached = false;
            _autoGatt.CccdEnabled = false;

            ServiceUuidTextBox.Text = _service.Uuid.ToString("D").ToUpperInvariant();
            WriteUuidTextBox.Text = _writeCharacteristic.Uuid.ToString("D").ToUpperInvariant();
            NotifyUuidTextBox.Text = _notifyCharacteristic.Uuid.ToString("D").ToUpperInvariant();

            AppendSystemLine("AUTO GATT");
            AppendSystemLine($"profile={bestProfileName}");
            AppendSystemLine($"service={BleUuid.Short(_service.Uuid)} [{BleUuid.Full(_service.Uuid)}]");
            AppendSystemLine($"write={BleUuid.Short(_writeCharacteristic.Uuid)} [{BleUuid.Full(_writeCharacteristic.Uuid)}]");
            AppendSystemLine($"notify={BleUuid.Short(_notifyCharacteristic.Uuid)} [{BleUuid.Full(_notifyCharacteristic.Uuid)}]");
            AppendSystemLine($"sameCharacteristic={ReferenceEquals(_writeCharacteristic, _notifyCharacteristic).ToString().ToLowerInvariant()}");
            if (isRadtel)
            {
                AppendSystemLine("PROFILE=RADTEL_RT950_KISS");
                AppendSystemLine($"service={BleUuid.Short(_service.Uuid)} rx={BleUuid.Short(_notifyCharacteristic.Uuid)} tx={BleUuid.Short(_writeCharacteristic.Uuid)} sameCharacteristic={ReferenceEquals(_writeCharacteristic, _notifyCharacteristic).ToString().ToLowerInvariant()}");
            }

            if (BleUuid.Is(_notifyCharacteristic.Uuid, "FFE1"))
                LogFfe1Properties(_notifyCharacteristic);

            return bestProfileName;
        }
        finally
        {
            _gattOperationGate.Release();
        }
    }

    private static bool IsRadtelDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        string n = name.Trim();
        return n.Contains("walkie-talkie", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("RT-950", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("RT950", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("RADTEL", StringComparison.OrdinalIgnoreCase);
    }

    private void LogFfe1Properties(GattCharacteristic characteristic)
    {
        GattCharacteristicProperties p = characteristic.CharacteristicProperties;
        AppendSystemLine("FFE1 PROPERTIES");
        AppendSystemLine($"UUID={BleUuid.Full(characteristic.Uuid)}");
        AppendSystemLine($"BROADCAST={YesNo(p.HasFlag(GattCharacteristicProperties.Broadcast))}");
        AppendSystemLine($"READ={YesNo(p.HasFlag(GattCharacteristicProperties.Read))}");
        AppendSystemLine($"WRITE={YesNo(p.HasFlag(GattCharacteristicProperties.Write))}");
        AppendSystemLine($"WRITE_WITHOUT_RESPONSE={YesNo(p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))}");
        AppendSystemLine($"WRITE_NO_RESPONSE={YesNo(p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))}");
        AppendSystemLine($"NOTIFY={YesNo(p.HasFlag(GattCharacteristicProperties.Notify))}");
        AppendSystemLine($"INDICATE={YesNo(p.HasFlag(GattCharacteristicProperties.Indicate))}");
        AppendSystemLine($"AUTHENTICATED_SIGNED_WRITES={YesNo(p.HasFlag(GattCharacteristicProperties.AuthenticatedSignedWrites))}");
        AppendSystemLine($"EXTENDED_PROPERTIES={YesNo(p.HasFlag(GattCharacteristicProperties.ExtendedProperties))}");
    }

    private void AttachNotifyHandler(GattCharacteristic characteristic)
    {
        if (ReferenceEquals(_notifyHandlerCharacteristic, characteristic))
            return;

        if (_notifyHandlerCharacteristic != null)
        {
            try { _notifyHandlerCharacteristic.ValueChanged -= NotifyCharacteristic_ValueChanged; } catch { }
        }

        characteristic.ValueChanged += NotifyCharacteristic_ValueChanged;
        _notifyHandlerCharacteristic = characteristic;
        _ffe1HandlerAttached = BleUuid.Is(characteristic.Uuid, "FFE1");

        if (_autoGatt.IsAvailable && ReferenceEquals(_autoGatt.NotifyCharacteristic, characteristic))
            _autoGatt.NotifyHandlerAttached = true;

        if (_ffe1HandlerAttached)
            AppendSystemLine("FFE1 VALUECHANGED HANDLER ATTACHED");
        else
            AppendSystemLine($"VALUECHANGED HANDLER ATTACHED uuid={BleUuid.Short(characteristic.Uuid)}");
    }

    private async Task<bool> EnableTerminalNotificationsAsync(GattCharacteristic characteristic)
    {
        GattClientCharacteristicConfigurationDescriptorValue cccd;
        if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            cccd = GattClientCharacteristicConfigurationDescriptorValue.Notify;
        else if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
            cccd = GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        else
            throw new InvalidOperationException("The receive characteristic supports neither Notify nor Indicate.");

        string shortUuid = BleUuid.Short(characteristic.Uuid);
        AppendSystemLine($"{shortUuid} CCCD WRITE START value={cccd}");

        await _gattOperationGate.WaitAsync();
        try
        {
            GattWriteResult result = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(cccd);
            AppendSystemLine($"{shortUuid} CCCD WRITE RESULT={result.Status} statusCode={(int)result.Status} protocol={ProtocolText(result.ProtocolError)}");

            if (result.Status == GattCommunicationStatus.Success)
            {
                if (BleUuid.Is(characteristic.Uuid, "FFE1"))
                    AppendSystemLine("FFE1 NOTIFY ACTIVE");
                else
                    AppendSystemLine($"DATA CHANNEL READY uuid={shortUuid}");
                return true;
            }

            await LogCccdFailureDiagnosticsAsync(characteristic, result.Status, result.ProtocolError, null);
            return false;
        }
        catch (Exception ex)
        {
            await LogCccdFailureDiagnosticsAsync(characteristic, null, null, ex);
            return false;
        }
        finally
        {
            _gattOperationGate.Release();
        }
    }

    private async Task LogCccdFailureDiagnosticsAsync(
        GattCharacteristic characteristic,
        GattCommunicationStatus? status,
        byte? protocolError,
        Exception? exception)
    {
        string source = _autoGatt.IsAvailable && ReferenceEquals(_autoGatt.NotifyCharacteristic, characteristic)
            ? "AUTO-DETECT"
            : "MANUAL";
        string serviceUuid = _service != null ? BleUuid.Full(_service.Uuid) : "unknown";
        string sessionStatus = "unknown";

        try
        {
            GattSession? session = _service?.Session;
            if (session != null)
                sessionStatus = session.SessionStatus.ToString();
        }
        catch
        {
            // Diagnostics must not disturb the active connection.
        }

        AppendSystemLine($"{BleUuid.Short(characteristic.Uuid)} CCCD FAILED");
        AppendSystemLine($"status={(status?.ToString() ?? "EXCEPTION")} statusCode={(status.HasValue ? ((int)status.Value).ToString(CultureInfo.InvariantCulture) : "n/a")} protocol={ProtocolText(protocolError)}");
        AppendSystemLine($"service={serviceUuid}");
        AppendSystemLine($"serviceSource={source}");
        AppendSystemLine($"characteristicSource={source}");
        AppendSystemLine($"cachedReused={(_autoGatt.IsAvailable && ReferenceEquals(_autoGatt.NotifyCharacteristic, characteristic)).ToString().ToLowerInvariant()}");
        AppendSystemLine($"connectionState={_device?.ConnectionStatus.ToString() ?? "unknown"}");
        AppendSystemLine($"gattSessionStatus={sessionStatus}");
        if (exception != null)
        {
            AppendSystemLine($"HRESULT=0x{exception.HResult:X8}");
            AppendSystemLine($"exception={exception.Message}");
        }
        await Task.CompletedTask;
    }

    private void ResetConnectionState()
    {
        _bleConnected = false;
        _servicesDiscovered = false;
        _ffe1Found = false;
        _ffe1HandlerAttached = false;
        _ffe1CccdEnabled = false;
        _radtelKissReady = false;
    }

    private void LogConnectionStateSnapshot()
    {
        AppendSystemLine($"STATE BLE={(_bleConnected ? "CONNECTED" : "DISCONNECTED")}");
        AppendSystemLine($"STATE GATT={(_servicesDiscovered ? "DISCOVERED" : "NOT_DISCOVERED")}");
        AppendSystemLine($"STATE FFE1={(_ffe1Found ? "FOUND" : "NOT_FOUND")}");
        AppendSystemLine($"STATE FFE1_HANDLER={(_ffe1HandlerAttached ? "YES" : "NO")}");
        AppendSystemLine($"STATE FFE1_CCCD={(_ffe1CccdEnabled ? "SUCCESS" : "NOT_READY")}");
        AppendSystemLine($"STATE RT950_KISS={(_radtelKissReady ? "READY" : "NOT_READY")}");
    }

    private void LogAutoKissFrame(GattCharacteristic characteristic, KissFrame frame)
    {
        string cmd = frame.PortCommand.HasValue ? frame.PortCommand.Value.ToString("X2") : "--";
        AppendSystemLine("KISS RX");
        AppendSystemLine($"SOURCE={BleUuid.Short(characteristic.Uuid)}");
        AppendSystemLine($"LEN={frame.Payload.Length}");
        AppendSystemLine($"PORT/CMD={cmd}");
        AppendSystemLine($"RAW HEX={Hex(frame.Raw)}");
        AppendSystemLine($"UNESCAPED={Hex(frame.Payload)}");
    }

    private static string YesNo(bool value) => value ? "YES" : "NO";
    private static string Hex(byte[] data) => BitConverter.ToString(data).Replace('-', ' ');
    private static string ProtocolText(byte? protocolError) => protocolError.HasValue ? $"0x{protocolError.Value:X2}" : "none";

    private static int ScoreUartCandidate(Guid serviceUuid, GattCharacteristic write, GattCharacteristic notify, out string profileName)
    {
        Guid nusService = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
        Guid nusWrite = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
        Guid nusNotify = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");
        Guid hm10Service = Guid.Parse("0000FFE0-0000-1000-8000-00805F9B34FB");
        Guid hm10Data = Guid.Parse("0000FFE1-0000-1000-8000-00805F9B34FB");

        if (serviceUuid == nusService && write.Uuid == nusWrite && notify.Uuid == nusNotify)
        {
            profileName = "Nordic UART Service (NUS)";
            return 10000;
        }

        if (serviceUuid == hm10Service && write.Uuid == hm10Data && notify.Uuid == hm10Data)
        {
            profileName = "HM-10 / FFE0-FFE1 UART";
            return 9500;
        }

        int score = 100;
        if (write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)) score += 35;
        if (write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)) score += 20;
        if (notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)) score += 35;
        if (notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate)) score += 15;
        if (write.Uuid == notify.Uuid) score += 30;
        if (!IsBluetoothBaseUuid(serviceUuid)) score += 15;

        profileName = write.Uuid == notify.Uuid
            ? "Generic BLE-UART (shared Write/Notify characteristic)"
            : "Generic BLE-UART (auto detected)";
        return score;
    }

    private static bool IsBluetoothSigStandardService(Guid uuid)
    {
        string n = uuid.ToString("N");
        const string bluetoothBaseSuffix = "00001000800000805f9b34fb";
        if (n.Length != 32 || !n.StartsWith("0000", StringComparison.OrdinalIgnoreCase) ||
            !n.EndsWith(bluetoothBaseSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!ushort.TryParse(n.Substring(4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort id))
            return false;

        return id >= 0x1800 && id <= 0x18FF;
    }

    private static bool IsBluetoothBaseUuid(Guid uuid)
    {
        string n = uuid.ToString("N");
        const string bluetoothBaseSuffix = "00001000800000805f9b34fb";
        return n.Length == 32 && n.StartsWith("0000", StringComparison.OrdinalIgnoreCase) &&
               n.EndsWith(bluetoothBaseSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected)
            return;

        Dispatcher.BeginInvoke(async () =>
        {
            if (_manualDisconnect) return;

            ulong? address = _connectedAddress;
            string name = sender.Name;
            AppendSystemLine("DISCONNECTED by device / BLE stack");
            SetUiConnected(false);
            ConnectionTextBlock.Text = "Disconnected";

            if (AutoReconnectCheckBox.IsChecked == true && address.HasValue)
            {
                if (Interlocked.Exchange(ref _reconnectInProgress, 1) != 0) return;
                try
                {
                    SetStatus("Disconnected; auto reconnect pending...");
                    await Task.Delay(1500);
                    if (!_manualDisconnect && AutoReconnectCheckBox.IsChecked == true)
                        await ConnectByAddressAsync(address.Value, name, true);
                }
                finally
                {
                    Interlocked.Exchange(ref _reconnectInProgress, 0);
                }
            }
            else
            {
                CleanupConnection();
            }
        });
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _manualDisconnect = true;
        await DisableNotificationsBestEffortAsync();
        AppendSystemLine("DISCONNECTED by user");
        CleanupConnection();
        SetUiConnected(false);
        ConnectionTextBlock.Text = "Disconnected";
        SetStatus("Disconnected");
    }

    private async Task DisableNotificationsBestEffortAsync()
    {
        if (_notifyCharacteristic == null) return;
        await _gattOperationGate.WaitAsync();
        try
        {
            GattWriteResult result = await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None);
            AppendSystemLine($"CCCD DISABLE uuid={BleUuid.Short(_notifyCharacteristic.Uuid)} status={result.Status} statusCode={(int)result.Status} protocol={ProtocolText(result.ProtocolError)}");
        }
        catch (Exception ex)
        {
            AppendSystemLine($"CCCD DISABLE ERROR uuid={BleUuid.Short(_notifyCharacteristic.Uuid)} HRESULT=0x{ex.HResult:X8} exception={ex.Message}");
        }
        finally
        {
            _gattOperationGate.Release();
        }
    }

    private void CleanupConnection()
    {
        try
        {
            if (_gattInspectorWindow != null)
            {
                _gattInspectorWindow.Close();
                _gattInspectorWindow = null;
            }
        }
        catch { }

        try
        {
            if (_notifyHandlerCharacteristic != null)
                _notifyHandlerCharacteristic.ValueChanged -= NotifyCharacteristic_ValueChanged;
        }
        catch { }
        _notifyHandlerCharacteristic = null;

        try
        {
            if (_device != null)
                _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
        }
        catch { }

        _notifyCharacteristic = null;
        _writeCharacteristic = null;
        _autoGatt = new AutoDetectedGattContext();
        _autoKissDecoder = new KissStreamDecoder();

        try { _service?.Dispose(); } catch { }
        _service = null;

        try { _device?.Dispose(); } catch { }
        _device = null;
        _connectedAddress = null;
        _connectedAt = null;
        ResetConnectionState();
    }

    private void NotifyCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using DataReader reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] data = new byte[checked((int)reader.UnconsumedBufferLength)];
            reader.ReadBytes(data);
            Interlocked.Add(ref _rxBytes, data.Length);

            Dispatcher.BeginInvoke(() =>
            {
                if (_autoGatt.IsRadtelRt950Kiss && BleUuid.Is(sender.Uuid, "FFE1"))
                {
                    // RAW bytes are logged before any KISS parsing. This is intentionally independent
                    // from frame validity so malformed/fragmented traffic is still visible.
                    AppendSystemLine("RAW BLE NOTIFICATION");
                    AppendSystemLine($"UUID={BleUuid.Short(sender.Uuid)} [{BleUuid.Full(sender.Uuid)}]");
                    AppendSystemLine($"LEN={data.Length}");
                    AppendSystemLine($"HEX={Hex(data)}");

                    foreach (KissFrame frame in _autoKissDecoder.Push(data))
                        LogAutoKissFrame(sender, frame);
                }

                AppendRx(data);
                UpdateCounters();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() => AppendSystemLine($"RX ERROR: {ex.Message}"));
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendCurrentTxAsync();

    private async void TxTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await SendCurrentTxAsync();
    }

    private async Task SendCurrentTxAsync()
    {
        if (_writeCharacteristic == null)
        {
            MessageBox.Show(this, "Not connected.", "BLE Serial Terminal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        byte[] payload;
        try
        {
            payload = BuildTxPayload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid TX data", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (payload.Length == 0) return;

        int chunkSize = 20;
        if (!int.TryParse(ChunkSizeTextBox.Text.Trim(), out chunkSize) || chunkSize < 1 || chunkSize > 512)
        {
            MessageBox.Show(this, "TX chunk bytes must be from 1 to 512.", "BLE Serial Terminal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool canWriteNoResponse = _writeCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        bool canWriteResponse = _writeCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
        if (!canWriteNoResponse && !canWriteResponse)
        {
            MessageBox.Show(this, "Selected characteristic is not writable.", "BLE Serial Terminal", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var writeOption = canWriteNoResponse ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
        SendButton.IsEnabled = false;
        await _gattOperationGate.WaitAsync();
        try
        {
            for (int offset = 0; offset < payload.Length; offset += chunkSize)
            {
                int count = Math.Min(chunkSize, payload.Length - offset);
                byte[] chunk = new byte[count];
                System.Buffer.BlockCopy(payload, offset, chunk, 0, count);

                using var writer = new DataWriter();
                writer.WriteBytes(chunk);
                IBuffer buffer = writer.DetachBuffer();

                GattWriteResult result = await _writeCharacteristic.WriteValueWithResultAsync(buffer, writeOption);
                if (result.Status != GattCommunicationStatus.Success)
                    throw new InvalidOperationException($"GATT write failed at byte {offset}: {result.Status}.");
            }

            Interlocked.Add(ref _txBytes, payload.Length);
            if (LocalEchoCheckBox.IsChecked == true)
                AppendTx(payload);
            UpdateCounters();
            TxTextBox.Clear();
        }
        catch (Exception ex)
        {
            AppendSystemLine($"TX ERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, "BLE write failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _gattOperationGate.Release();
            SendButton.IsEnabled = _writeCharacteristic != null;
        }
    }

    private byte[] BuildTxPayload()
    {
        string input = TxTextBox.Text;
        byte[] body;

        if (TxModeComboBox.SelectedIndex == 1)
        {
            string compact = new string(input.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != ':' && c != ',').ToArray());
            if (compact.Length == 0) return Array.Empty<byte>();
            if ((compact.Length & 1) != 0)
                throw new FormatException("HEX data must contain an even number of hex digits.");

            body = new byte[compact.Length / 2];
            for (int i = 0; i < body.Length; i++)
            {
                string pair = compact.Substring(i * 2, 2);
                if (!byte.TryParse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out body[i]))
                    throw new FormatException($"Invalid HEX byte: {pair}");
            }
        }
        else
        {
            body = Encoding.UTF8.GetBytes(input);
        }

        byte[] ending = LineEndingComboBox.SelectedIndex switch
        {
            1 => new byte[] { 0x0D },
            2 => new byte[] { 0x0A },
            3 => new byte[] { 0x0D, 0x0A },
            _ => Array.Empty<byte>()
        };

        if (ending.Length == 0) return body;
        byte[] result = new byte[body.Length + ending.Length];
        System.Buffer.BlockCopy(body, 0, result, 0, body.Length);
        System.Buffer.BlockCopy(ending, 0, result, body.Length, ending.Length);
        return result;
    }

    private void AppendRx(byte[] data)
    {
        switch (RxViewComboBox.SelectedIndex)
        {
            case 1:
                AppendHexLine("RX HEX", data);
                break;
            case 2:
                AppendAsciiChunk(data);
                AppendHexLine("RX HEX", data);
                break;
            default:
                AppendAsciiChunk(data);
                break;
        }
    }

    private void AppendAsciiChunk(byte[] data)
    {
        string chunk = Encoding.UTF8.GetString(data);

        foreach (char c in chunk)
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                if (c == '\n')
                    continue;
            }

            if (c == '\r')
            {
                FlushAsciiLine();
                _pendingCr = true;
                continue;
            }

            if (c == '\n')
            {
                FlushAsciiLine();
                continue;
            }

            if (c == '\t')
            {
                _rxAsciiLineBuffer.Append("    ");
            }
            else if (char.IsControl(c))
            {
                _rxAsciiLineBuffer.Append($"\\x{(int)c:X2}");
            }
            else
            {
                _rxAsciiLineBuffer.Append(c);
            }
        }
    }

    private void FlushAsciiLine()
    {
        string prefix = TimestampCheckBox.IsChecked == true ? $"[{DateTime.Now:HH:mm:ss.fff}] " : string.Empty;
        TerminalTextBox.AppendText($"{prefix}RX      {_rxAsciiLineBuffer}{Environment.NewLine}");
        _rxAsciiLineBuffer.Clear();
        ScrollTerminalIfNeeded();
    }

    private void AppendHexLine(string label, byte[] data)
    {
        string prefix = TimestampCheckBox.IsChecked == true ? $"[{DateTime.Now:HH:mm:ss.fff}] " : string.Empty;
        string hex = BitConverter.ToString(data).Replace('-', ' ');
        TerminalTextBox.AppendText($"{prefix}{label}  {hex}{Environment.NewLine}");
        ScrollTerminalIfNeeded();
    }

    private void AppendTx(byte[] data)
    {
        string prefix = TimestampCheckBox.IsChecked == true ? $"[{DateTime.Now:HH:mm:ss.fff}] " : string.Empty;
        string mode = TxModeComboBox.SelectedIndex == 1 ? "HEX" : "TXT";
        string text = TxModeComboBox.SelectedIndex == 1
            ? BitConverter.ToString(data).Replace('-', ' ')
            : DisplayTxText(data);
        TerminalTextBox.AppendText($"{prefix}TX {mode}  {text}{Environment.NewLine}");
        ScrollTerminalIfNeeded();
    }

    private void AppendSystemLine(string message)
    {
        string prefix = TimestampCheckBox.IsChecked == true ? $"[{DateTime.Now:HH:mm:ss.fff}] " : string.Empty;
        TerminalTextBox.AppendText($"{prefix}*** {message}{Environment.NewLine}");
        ScrollTerminalIfNeeded();
    }

    private static string DisplayTxText(byte[] data)
    {
        string s = Encoding.UTF8.GetString(data);
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\r') sb.Append("\\r");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\t') sb.Append("\\t");
            else if (char.IsControl(c)) sb.Append($"\\x{(int)c:X2}");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private void ScrollTerminalIfNeeded()
    {
        if (AutoScrollCheckBox.IsChecked == true)
            TerminalTextBox.ScrollToEnd();
    }

    private void ClearRxButton_Click(object sender, RoutedEventArgs e)
    {
        _rxAsciiLineBuffer.Clear();
        _pendingCr = false;
        TerminalTextBox.Clear();
        _rxBytes = 0;
        _txBytes = 0;
        UpdateCounters();
    }

    private void SaveLogButton_Click(object sender, RoutedEventArgs e) => ExportTerminalLog();

    private void ExportLogMenuItem_Click(object sender, RoutedEventArgs e) => ExportTerminalLog();

    private void ExportTerminalLog()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export BLE serial log",
            Filter = "Text files (*.txt)|*.txt|Log files (*.log)|*.log|All files (*.*)|*.*",
            FileName = $"BLESerial_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var export = new StringBuilder();
        export.AppendLine("BLE Serial Terminal - Log Export");
        export.AppendLine($"Exported: {DateTime.Now:O}");
        export.AppendLine($"Connection: {ConnectionTextBlock.Text}");
        export.AppendLine($"RX bytes: {Interlocked.Read(ref _rxBytes)}");
        export.AppendLine($"TX bytes: {Interlocked.Read(ref _txBytes)}");
        if (_connectedAt.HasValue)
            export.AppendLine($"Connected at: {_connectedAt.Value:O}");
        if (_autoGatt.IsAvailable)
        {
            export.AppendLine($"Auto profile: {_autoGatt.ProfileName}");
            export.AppendLine($"Auto service: {BleUuid.Full(_autoGatt.Service!.Uuid)}");
            export.AppendLine($"Auto write: {BleUuid.Full(_autoGatt.WriteCharacteristic!.Uuid)}");
            export.AppendLine($"Auto notify: {BleUuid.Full(_autoGatt.NotifyCharacteristic!.Uuid)}");
            export.AppendLine($"Auto sameCharacteristic: {_autoGatt.SameCharacteristic}");
            export.AppendLine($"Auto handlerAttached: {_autoGatt.NotifyHandlerAttached}");
            export.AppendLine($"Auto cccdEnabled: {_autoGatt.CccdEnabled}");
        }
        export.AppendLine($"State BLE_CONNECTED={_bleConnected} SERVICES_DISCOVERED={_servicesDiscovered} FFE1_FOUND={_ffe1Found} FFE1_HANDLER={_ffe1HandlerAttached} FFE1_CCCD={_ffe1CccdEnabled} RT950_KISS_READY={_radtelKissReady}");
        export.AppendLine(new string('-', 72));
        export.Append(TerminalTextBox.Text);

        System.IO.File.WriteAllText(dialog.FileName, export.ToString(), new UTF8Encoding(false));
        SetStatus($"Log exported: {dialog.FileName}");
    }

    private bool TryReadChunkSize(out int chunkSize)
    {
        if (!int.TryParse(ChunkSizeTextBox.Text.Trim(), out chunkSize) || chunkSize < 1 || chunkSize > 512)
        {
            MessageBox.Show(this, "TX chunk bytes must be from 1 to 512.", "BLE Serial Terminal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private bool TryReadProfile(out Guid serviceUuid, out Guid writeUuid, out Guid notifyUuid, out int chunkSize)
    {
        // Assign every out parameter before any early return.
        serviceUuid = Guid.Empty;
        writeUuid = Guid.Empty;
        notifyUuid = Guid.Empty;
        chunkSize = 20;
        if (!BleUuid.TryParse(ServiceUuidTextBox.Text, out serviceUuid))
        {
            MessageBox.Show(this, "Invalid Service UUID.", "BLE Serial Terminal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!BleUuid.TryParse(WriteUuidTextBox.Text, out writeUuid))
        {
            MessageBox.Show(this, "Invalid Write UUID.", "BLE Serial Terminal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!BleUuid.TryParse(NotifyUuidTextBox.Text, out notifyUuid))
        {
            MessageBox.Show(this, "Invalid Notify UUID.", "BLE Serial Terminal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!int.TryParse(ChunkSizeTextBox.Text.Trim(), out chunkSize) || chunkSize < 1 || chunkSize > 512)
        {
            MessageBox.Show(this, "TX chunk bytes must be from 1 to 512.", "BLE Serial Terminal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void SetUiConnected(bool connected)
    {
        ConnectButton.IsEnabled = !connected;
        DisconnectButton.IsEnabled = connected;
        GattInspectorButton.IsEnabled = connected;
        SendButton.IsEnabled = connected;
    }

    private void SetUiConnecting(bool connecting)
    {
        if (connecting)
        {
            ConnectButton.IsEnabled = false;
            DisconnectButton.IsEnabled = false;
            GattInspectorButton.IsEnabled = false;
            SendButton.IsEnabled = false;
        }
        else if (_writeCharacteristic == null)
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void SetStatus(string text) => StatusTextBlock.Text = text;

    private void UpdateCounters() => CountersTextBlock.Text = $"RX {Interlocked.Read(ref _rxBytes)} B   TX {Interlocked.Read(ref _txBytes)} B";

    private static string FormatBluetoothAddress(ulong address)
    {
        byte[] bytes = BitConverter.GetBytes(address);
        return string.Join(":", bytes.Take(6).Reverse().Select(b => b.ToString("X2")));
    }
}

public sealed class BleDeviceRow : INotifyPropertyChanged
{
    private string _name;
    private int _rssi;
    private DateTime _lastSeen;

    public BleDeviceRow(ulong bluetoothAddress, string name, int rssi, DateTime lastSeen)
    {
        BluetoothAddress = bluetoothAddress;
        _name = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
        _rssi = rssi;
        _lastSeen = lastSeen;
    }

    public ulong BluetoothAddress { get; }
    public string AddressText => string.Join(":", BitConverter.GetBytes(BluetoothAddress).Take(6).Reverse().Select(b => b.ToString("X2")));

    public string Name
    {
        get => _name;
        set
        {
            string next = string.IsNullOrWhiteSpace(value) ? "(unnamed)" : value;
            if (_name == next) return;
            _name = next;
            OnPropertyChanged(nameof(Name));
        }
    }

    public int Rssi
    {
        get => _rssi;
        set
        {
            if (_rssi == value) return;
            _rssi = value;
            OnPropertyChanged(nameof(Rssi));
            OnPropertyChanged(nameof(RssiText));
        }
    }

    public string RssiText => $"{Rssi} dBm";

    public DateTime LastSeen
    {
        get => _lastSeen;
        set
        {
            _lastSeen = value;
            OnPropertyChanged(nameof(LastSeen));
            OnPropertyChanged(nameof(LastSeenText));
        }
    }

    public string LastSeenText => LastSeen.ToString("HH:mm:ss");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
