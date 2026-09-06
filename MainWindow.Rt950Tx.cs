using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private readonly SerialTaskQueue _manualTxQueue = new();
    private readonly List<CommandRowData> _commandRows = new();
    private readonly Dictionary<string, CommandRowUi> _commandRowUi = new(StringComparer.Ordinal);
    private DispatcherTimer? _gattRoutingRefreshTimer;
    private DispatcherTimer? _txPreferenceSaveTimer;
    private bool _routingUiUpdating;
    private bool _commandWorkspaceLoading;
    private string _routingConnectionKey = string.Empty;
    private string _notifyReadyLoggedKey = string.Empty;
    private CommandWorkspaceSettings _commandWorkspaceSettings = new();
    private StackPanel? _dynamicCommandRowsPanel;
    private TextBlock? _dynamicCommandSummary;

    private static string TxCommandSettingsFile => System.IO.Path.Combine(SettingsDirectory, "tx-command-settings.json");

    private sealed class CommandRowUi
    {
        public required CommandRowData Model { get; init; }
        public required Border Root { get; init; }
        public required TextBlock Number { get; init; }
        public required TextBox Label { get; init; }
        public required TextBox Data { get; init; }
        public required Button Send { get; init; }
        public required Button Clear { get; init; }
        public required Button Remove { get; init; }
        public required Button AdvancedButton { get; init; }
        public required Grid AdvancedGrid { get; init; }
        public required ComboBox Target { get; init; }
        public required ComboBox WriteType { get; init; }
        public required ComboBox TxMode { get; init; }
        public required ComboBox LineEnding { get; init; }
    }

    private void InitializeGattRoutingAndCommandWorkspace()
    {
        LoadTxCommandPreferences();
        InitializeDynamicCommandWorkspace();

        _gattRoutingRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _gattRoutingRefreshTimer.Tick += GattRoutingRefreshTimer_Tick;
        _gattRoutingRefreshTimer.Start();

        _txPreferenceSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _txPreferenceSaveTimer.Tick += TxPreferenceSaveTimer_Tick;

        ClearLiveGattRoutingControls();
    }

    private void ShutdownGattRoutingAndCommandWorkspace()
    {
        if (_gattRoutingRefreshTimer != null)
        {
            _gattRoutingRefreshTimer.Stop();
            _gattRoutingRefreshTimer.Tick -= GattRoutingRefreshTimer_Tick;
            _gattRoutingRefreshTimer = null;
        }

        if (_txPreferenceSaveTimer != null)
        {
            _txPreferenceSaveTimer.Stop();
            _txPreferenceSaveTimer.Tick -= TxPreferenceSaveTimer_Tick;
            _txPreferenceSaveTimer = null;
        }

        SaveTxCommandPreferences();
    }

    private void InitializeDynamicCommandWorkspace()
    {
        if (TxCommandsExpander == null)
            return;

        TxCommandsExpander.Header = "Commands";
        TxCommandsExpander.IsExpanded = true;
        TxCommandsExpander.MaxHeight = 260;

        var outer = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 6, 0, 0)
        };
        outer.SetResourceReference(Border.BackgroundProperty, "PanelBackgroundBrush");
        outer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var add = new Button
        {
            Content = "+ ADD COMMAND",
            MinWidth = 130,
            Height = 28,
            Padding = new Thickness(10, 2, 10, 2)
        };
        add.Click += (_, _) => AddCommandRow();
        DockPanel.SetDock(add, Dock.Left);
        header.Children.Add(add);

        _dynamicCommandSummary = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _dynamicCommandSummary.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundBrush");
        DockPanel.SetDock(_dynamicCommandSummary, Dock.Right);
        header.Children.Add(_dynamicCommandSummary);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _dynamicCommandRowsPanel = new StackPanel();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 210,
            Content = _dynamicCommandRowsPanel
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        outer.Child = root;
        TxCommandsExpander.Content = outer;

        if (_commandRows.Count == 0)
            _commandRows.Add(NewGeneralCommand("Command 1"));

        RebuildDynamicCommandRows();
    }

    private static CommandRowData NewGeneralCommand(string label) => new()
    {
        Label = label,
        Group = "General"
    };

    private void AddCommandRow(CommandRowData? seed = null)
    {
        CommandRowData model = seed?.Clone() ?? NewGeneralCommand($"Command {_commandRows.Count + 1}");
        if (string.IsNullOrWhiteSpace(model.Id) || _commandRows.Any(r => r.Id == model.Id))
            model.Id = Guid.NewGuid().ToString("N");
        _commandRows.Add(model);
        RebuildDynamicCommandRows();
        SaveTxCommandPreferences();
    }

    private void RemoveCommandRow(CommandRowData row)
    {
        if (_commandWorkspaceSettings.ConfirmRemove)
        {
            MessageBoxResult answer = MessageBox.Show(
                this,
                $"Remove command '{row.Label}'?",
                "Remove command",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        _commandRows.Remove(row);
        if (_commandRows.Count == 0)
            _commandRows.Add(NewGeneralCommand("Command 1"));
        RebuildDynamicCommandRows();
        SaveTxCommandPreferences();
    }

    private void RebuildDynamicCommandRows()
    {
        if (_dynamicCommandRowsPanel == null)
            return;

        _commandWorkspaceLoading = true;
        try
        {
            _dynamicCommandRowsPanel.Children.Clear();
            _commandRowUi.Clear();

            for (int index = 0; index < _commandRows.Count; index++)
            {
                CommandRowUi ui = CreateCommandRowUi(_commandRows[index], index + 1);
                _commandRowUi[ui.Model.Id] = ui;
                _dynamicCommandRowsPanel.Children.Add(ui.Root);
            }
        }
        finally
        {
            _commandWorkspaceLoading = false;
        }

        UpdateCommandWorkspaceSummary();
        RefreshCommandTargetChoices();
        RefreshDynamicCommandSendAvailability();
    }

    private CommandRowUi CreateCommandRowUi(CommandRowData row, int number)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5),
            Margin = new Thickness(0, 0, 0, 5)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var stack = new StackPanel();
        border.Child = stack;

        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        var numberText = new TextBlock
        {
            Text = number.ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(numberText, 0);
        main.Children.Add(numberText);

        var label = new TextBox { Text = row.Label, Margin = new Thickness(0, 0, 5, 0), VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        main.Children.Add(label);

        var data = new TextBox
        {
            Text = row.Command,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 5, 0),
            Padding = new Thickness(5),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(data, 2);
        main.Children.Add(data);

        var send = new Button { Content = "SEND", Margin = new Thickness(0, 0, 5, 0), Tag = row.Id };
        Grid.SetColumn(send, 3);
        main.Children.Add(send);

        var clear = new Button { Content = "CLR", Margin = new Thickness(0, 0, 5, 0), Tag = row.Id };
        Grid.SetColumn(clear, 4);
        main.Children.Add(clear);

        var advancedButton = new Button { Content = "⚙", Margin = new Thickness(0, 0, 5, 0), ToolTip = "Advanced per-command routing", Tag = row.Id };
        Grid.SetColumn(advancedButton, 5);
        main.Children.Add(advancedButton);

        var remove = new Button { Content = "X", ToolTip = "Remove command", Tag = row.Id };
        Grid.SetColumn(remove, 6);
        main.Children.Add(remove);

        stack.Children.Add(main);

        var advanced = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(34, 6, 0, 0) };
        advanced.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        advanced.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        advanced.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        advanced.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        advanced.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        advanced.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        advanced.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        advanced.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

        AddAdvancedLabel(advanced, "Target:", 0);
        var target = NewAdvancedCombo(1);
        advanced.Children.Add(target);
        AddAdvancedLabel(advanced, "Write:", 2);
        var write = NewAdvancedCombo(3, CommandOverrideValues.UseGlobal, CommandOverrideValues.WithResponse, CommandOverrideValues.WithoutResponse);
        advanced.Children.Add(write);
        AddAdvancedLabel(advanced, "Mode:", 4);
        var mode = NewAdvancedCombo(5, CommandOverrideValues.UseGlobal, CommandOverrideValues.Hex, CommandOverrideValues.Text);
        advanced.Children.Add(mode);
        AddAdvancedLabel(advanced, "Ending:", 6);
        var ending = NewAdvancedCombo(7, CommandOverrideValues.UseGlobal, CommandOverrideValues.None, CommandOverrideValues.Lf, CommandOverrideValues.Cr, CommandOverrideValues.CrLf);
        advanced.Children.Add(ending);
        stack.Children.Add(advanced);

        var ui = new CommandRowUi
        {
            Model = row,
            Root = border,
            Number = numberText,
            Label = label,
            Data = data,
            Send = send,
            Clear = clear,
            Remove = remove,
            AdvancedButton = advancedButton,
            AdvancedGrid = advanced,
            Target = target,
            WriteType = write,
            TxMode = mode,
            LineEnding = ending
        };

        label.TextChanged += (_, _) =>
        {
            if (_commandWorkspaceLoading) return;
            row.Label = label.Text;
            ScheduleCommandPreferenceSave();
        };
        data.TextChanged += (_, _) =>
        {
            if (_commandWorkspaceLoading) return;
            row.Command = data.Text;
            ScheduleCommandPreferenceSave();
        };
        data.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await QueueCommandRowAsync(row);
        };
        send.Click += async (_, _) => await QueueCommandRowAsync(row);
        clear.Click += (_, _) =>
        {
            data.Clear();
            row.Command = string.Empty;
            SaveTxCommandPreferences();
        };
        remove.Click += (_, _) => RemoveCommandRow(row);
        advancedButton.Click += (_, _) => advanced.Visibility = advanced.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        write.SelectionChanged += (_, _) =>
        {
            if (_commandWorkspaceLoading || write.SelectedItem == null) return;
            row.WriteTypeOverride = write.SelectedItem.ToString() ?? CommandOverrideValues.UseGlobal;
            ScheduleCommandPreferenceSave();
        };
        mode.SelectionChanged += (_, _) =>
        {
            if (_commandWorkspaceLoading || mode.SelectedItem == null) return;
            row.TxModeOverride = mode.SelectedItem.ToString() ?? CommandOverrideValues.UseGlobal;
            ScheduleCommandPreferenceSave();
        };
        ending.SelectionChanged += (_, _) =>
        {
            if (_commandWorkspaceLoading || ending.SelectedItem == null) return;
            row.LineEndingOverride = ending.SelectedItem.ToString() ?? CommandOverrideValues.UseGlobal;
            ScheduleCommandPreferenceSave();
        };
        target.SelectionChanged += (_, _) =>
        {
            if (_commandWorkspaceLoading || target.SelectedItem == null) return;
            row.TargetOverride = target.SelectedItem.ToString() ?? CommandOverrideValues.UseGlobal;
            ScheduleCommandPreferenceSave();
            RefreshDynamicCommandSendAvailability();
        };

        SelectComboValue(write, row.WriteTypeOverride);
        SelectComboValue(mode, row.TxModeOverride);
        SelectComboValue(ending, row.LineEndingOverride);
        return ui;
    }

    private static void AddAdvancedLabel(Grid grid, string text, int column)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(column == 0 ? 0 : 8, 0, 4, 0) };
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private static ComboBox NewAdvancedCombo(int column, params string[] values)
    {
        var combo = new ComboBox { Height = 26, Margin = new Thickness(0, 0, 0, 0) };
        if (values.Length > 0)
            combo.ItemsSource = values;
        Grid.SetColumn(combo, column);
        return combo;
    }

    private static void SelectComboValue(ComboBox combo, string value)
    {
        object? match = combo.Items.Cast<object>().FirstOrDefault(x => string.Equals(x?.ToString(), value, StringComparison.OrdinalIgnoreCase));
        combo.SelectedItem = match ?? combo.Items.Cast<object>().FirstOrDefault();
    }

    private void UpdateCommandWorkspaceSummary()
    {
        if (_dynamicCommandSummary != null)
            _dynamicCommandSummary.Text = $"{_commandRows.Count} command(s) • no fixed row limit";
        if (TxCommandsExpander != null)
            TxCommandsExpander.Header = $"Commands ({_commandRows.Count})";
    }

    private void RefreshCommandTargetChoices()
    {
        List<string> choices = new() { CommandOverrideValues.UseGlobal };
        foreach (GattCharacteristic characteristic in CurrentServiceCharacteristics().Where(IsWritableCharacteristic))
        {
            string shortUuid = BleUuid.Short(characteristic.Uuid);
            if (!choices.Contains(shortUuid, StringComparer.OrdinalIgnoreCase))
                choices.Add(shortUuid);
        }

        _commandWorkspaceLoading = true;
        try
        {
            foreach (CommandRowUi ui in _commandRowUi.Values)
            {
                string stored = ui.Model.TargetOverride;
                var rowChoices = choices.ToList();
                if (!string.IsNullOrWhiteSpace(stored) && stored != CommandOverrideValues.UseGlobal &&
                    !rowChoices.Contains(stored, StringComparer.OrdinalIgnoreCase))
                    rowChoices.Add(stored);
                ui.Target.ItemsSource = rowChoices;
                SelectComboValue(ui.Target, stored);
            }
        }
        finally
        {
            _commandWorkspaceLoading = false;
        }
    }

    private void GattRoutingRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!_servicesDiscovered || _service == null || !_connectedAddress.HasValue || !_connectedAt.HasValue)
        {
            if (!string.IsNullOrEmpty(_routingConnectionKey))
            {
                _routingConnectionKey = string.Empty;
                _notifyReadyLoggedKey = string.Empty;
                ClearLiveGattRoutingControls();
            }
            return;
        }

        string key = $"{_connectedAddress.Value:X12}|{_connectedAt.Value.Ticks}|{_service.Uuid:D}";
        if (!string.Equals(key, _routingConnectionKey, StringComparison.Ordinal))
        {
            _routingConnectionKey = key;
            _notifyReadyLoggedKey = string.Empty;
            PopulateLiveGattRoutingControls();
            LogCurrentGattServiceDiscovery();
        }

        LogNotifyActiveIfReady(key);
        UpdateTxSendAvailability();
    }

    private void PopulateLiveGattRoutingControls()
    {
        if (_service == null || ServiceRouteComboBox == null)
            return;

        List<GattCharacteristic> characteristics = CurrentServiceCharacteristics();
        _routingUiUpdating = true;
        try
        {
            ServiceRouteComboBox.ItemsSource = new[] { BleUuid.Short(_service.Uuid) };
            ServiceRouteComboBox.SelectedIndex = 0;
            ServiceRouteComboBox.IsEnabled = false;

            List<GattCharacteristicChoice> notifyChoices = characteristics
                .Select(c => new GattCharacteristicChoice(c))
                .Where(c => c.CanNotify)
                .OrderBy(c => c.ShortUuid, StringComparer.OrdinalIgnoreCase)
                .ToList();
            NotifyRouteComboBox.ItemsSource = notifyChoices;
            NotifyRouteComboBox.SelectedItem = notifyChoices.FirstOrDefault(c => _notifyCharacteristic != null && c.Characteristic.Uuid == _notifyCharacteristic.Uuid);

            List<GattCharacteristicChoice> writeChoices = characteristics
                .Select(c => new GattCharacteristicChoice(c))
                .Where(c => c.CanWrite)
                .OrderBy(c => c.ShortUuid, StringComparer.OrdinalIgnoreCase)
                .ToList();
            WriteRouteComboBox.ItemsSource = writeChoices;
            WriteRouteComboBox.SelectedItem = writeChoices.FirstOrDefault(c => _writeCharacteristic != null && c.Characteristic.Uuid == _writeCharacteristic.Uuid);
            if (WriteTypeComboBox.SelectedIndex < 0)
                WriteTypeComboBox.SelectedIndex = 0;
        }
        finally
        {
            _routingUiUpdating = false;
        }

        RoutingStatusTextBlock.Text = "Generic GATT routing active. Optional RT950/KISS modules do not own this route.";
        RefreshCommandTargetChoices();
        UpdateTxSendAvailability();
    }

    private List<GattCharacteristic> CurrentServiceCharacteristics()
    {
        if (_service != null && _autoGatt.Service != null && ReferenceEquals(_service, _autoGatt.Service) && _autoGatt.ServiceCharacteristics.Count > 0)
        {
            return _autoGatt.ServiceCharacteristics.GroupBy(c => c.Uuid).Select(g => g.First()).ToList();
        }

        var result = new List<GattCharacteristic>();
        if (_notifyCharacteristic != null)
            result.Add(_notifyCharacteristic);
        if (_writeCharacteristic != null && result.All(c => c.Uuid != _writeCharacteristic.Uuid))
            result.Add(_writeCharacteristic);
        return result;
    }

    private static bool IsWritableCharacteristic(GattCharacteristic characteristic)
    {
        GattCharacteristicProperties p = characteristic.CharacteristicProperties;
        return p.HasFlag(GattCharacteristicProperties.Write) || p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
    }

    private void ClearLiveGattRoutingControls()
    {
        if (ServiceRouteComboBox == null || NotifyRouteComboBox == null || WriteRouteComboBox == null || WriteTypeComboBox == null)
            return;

        _routingUiUpdating = true;
        try
        {
            ServiceRouteComboBox.ItemsSource = null;
            NotifyRouteComboBox.ItemsSource = null;
            WriteRouteComboBox.ItemsSource = null;
            WriteTypeComboBox.SelectedIndex = 0;
        }
        finally
        {
            _routingUiUpdating = false;
        }

        if (RoutingStatusTextBlock != null)
            RoutingStatusTextBlock.Text = "Connect to populate live GATT routing.";
        if (SendButton != null)
            SendButton.IsEnabled = false;
        RefreshCommandTargetChoices();
        RefreshDynamicCommandSendAvailability();
    }

    private void LogCurrentGattServiceDiscovery()
    {
        if (_service == null)
            return;

        AppendSystemLine("GATT SERVICE");
        AppendSystemLine($"UUID={BleUuid.Short(_service.Uuid)}");
        foreach (GattCharacteristic characteristic in CurrentServiceCharacteristics())
        {
            GattCharacteristicProperties p = characteristic.CharacteristicProperties;
            AppendSystemLine("GATT CHARACTERISTIC");
            AppendSystemLine($"UUID={BleUuid.Short(characteristic.Uuid)}");
            AppendSystemLine($"READ={YesNo(p.HasFlag(GattCharacteristicProperties.Read))}");
            AppendSystemLine($"WRITE={YesNo(p.HasFlag(GattCharacteristicProperties.Write))}");
            AppendSystemLine($"WRITE_WITHOUT_RESPONSE={YesNo(p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))}");
            AppendSystemLine($"NOTIFY={YesNo(p.HasFlag(GattCharacteristicProperties.Notify))}");
            AppendSystemLine($"INDICATE={YesNo(p.HasFlag(GattCharacteristicProperties.Indicate))}");
        }
    }

    private void LogNotifyActiveIfReady(string connectionKey)
    {
        if (string.Equals(_notifyReadyLoggedKey, connectionKey, StringComparison.Ordinal))
            return;
        if (!_ffe1CccdEnabled || _service == null || _notifyCharacteristic == null || !BleUuid.Is(_notifyCharacteristic.Uuid, "FFE1"))
            return;

        _notifyReadyLoggedKey = connectionKey;
        AppendSystemLine("BLE NOTIFY ACTIVE");
        AppendSystemLine($"SERVICE={BleUuid.Short(_service.Uuid)}");
        AppendSystemLine($"UUID={BleUuid.Short(_notifyCharacteristic.Uuid)}");
        AppendSystemLine("CCCD=SUCCESS");
    }

    private void WriteRouteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_routingUiUpdating || WriteRouteComboBox?.SelectedItem is not GattCharacteristicChoice choice)
            return;

        _writeCharacteristic = choice.Characteristic;
        WriteUuidTextBox.Text = BleUuid.Full(choice.Characteristic.Uuid);
        AppendSystemLine("MANUAL GATT TX ROUTE");
        AppendSystemLine($"SERVICE={(_service == null ? "-" : BleUuid.Short(_service.Uuid))}");
        AppendSystemLine($"WRITE_UUID={choice.ShortUuid}");
        AppendSystemLine($"AUTO_PROFILE_PRESERVED={(_autoGatt.IsAvailable ? "YES" : "N/A")}");
        UpdateTxSendAvailability();
    }

    private void WriteTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_routingUiUpdating)
            return;
        UpdateTxSendAvailability();
    }

    private async void NotifyRouteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_routingUiUpdating || NotifyRouteComboBox?.SelectedItem is not GattCharacteristicChoice choice)
            return;
        if (_notifyCharacteristic != null && _notifyCharacteristic.Uuid == choice.Characteristic.Uuid)
            return;
        await SwitchNotifyCharacteristicAsync(choice.Characteristic);
    }

    private async Task SwitchNotifyCharacteristicAsync(GattCharacteristic target)
    {
        if (!target.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) && !target.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
        {
            MessageBox.Show(this, "Selected characteristic does not support Notify or Indicate.", "GATT routing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        GattCharacteristic? previous = _notifyCharacteristic;
        if (SendButton != null) SendButton.IsEnabled = false;
        try
        {
            if (previous != null)
                await DisableNotificationsForCharacteristicBestEffortAsync(previous);

            _notifyCharacteristic = target;
            NotifyUuidTextBox.Text = BleUuid.Full(target.Uuid);
            AttachNotifyHandler(target);
            SyncMainNotificationCaptureHandler();
            bool ready = await EnableTerminalNotificationsAsync(target);
            if (!ready)
                throw new InvalidOperationException($"Notify subscription failed for {BleUuid.Short(target.Uuid)}.");

            _ffe1Found = BleUuid.Is(target.Uuid, "FFE1");
            _ffe1CccdEnabled = _ffe1Found && ready;
            _radtelKissReady = _autoGatt.IsRadtelRt950Kiss && _ffe1CccdEnabled;
            AppendSystemLine($"MANUAL RX ROUTE ACTIVE UUID={BleUuid.Short(target.Uuid)}");
            _notifyReadyLoggedKey = string.Empty;
        }
        catch (Exception ex)
        {
            AppendSystemLine($"MANUAL RX ROUTE ERROR: {ex.Message}");
            if (previous != null && !ReferenceEquals(previous, target))
            {
                try
                {
                    _notifyCharacteristic = previous;
                    NotifyUuidTextBox.Text = BleUuid.Full(previous.Uuid);
                    AttachNotifyHandler(previous);
                    SyncMainNotificationCaptureHandler();
                    bool restored = await EnableTerminalNotificationsAsync(previous);
                    _ffe1Found = BleUuid.Is(previous.Uuid, "FFE1");
                    _ffe1CccdEnabled = _ffe1Found && restored;
                    _radtelKissReady = _autoGatt.IsRadtelRt950Kiss && _ffe1CccdEnabled;
                    AppendSystemLine($"RX ROUTE RESTORED UUID={BleUuid.Short(previous.Uuid)} status={(restored ? "SUCCESS" : "FAIL")}");
                }
                catch (Exception restoreEx)
                {
                    AppendSystemLine($"RX ROUTE RESTORE ERROR: {restoreEx.Message}");
                }
            }
            MessageBox.Show(this, ex.Message, "GATT notify routing failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _routingUiUpdating = true;
            try
            {
                if (NotifyRouteComboBox.ItemsSource is IEnumerable<GattCharacteristicChoice> choices && _notifyCharacteristic != null)
                    NotifyRouteComboBox.SelectedItem = choices.FirstOrDefault(c => c.Characteristic.Uuid == _notifyCharacteristic.Uuid);
            }
            finally
            {
                _routingUiUpdating = false;
            }
            UpdateTxSendAvailability();
        }
    }

    private async Task DisableNotificationsForCharacteristicBestEffortAsync(GattCharacteristic characteristic)
    {
        await _gattOperationGate.WaitAsync();
        try
        {
            GattWriteResult result = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
            AppendSystemLine($"CCCD ROUTE DISABLE uuid={BleUuid.Short(characteristic.Uuid)} status={result.Status} statusCode={(int)result.Status} protocol={ProtocolText(result.ProtocolError)}");
        }
        catch (Exception ex)
        {
            AppendSystemLine($"CCCD ROUTE DISABLE ERROR uuid={BleUuid.Short(characteristic.Uuid)} HRESULT=0x{ex.HResult:X8} exception={ex.Message}");
        }
        finally
        {
            _gattOperationGate.Release();
        }
    }

    private async Task QueueCommandRowAsync(CommandRowData row)
    {
        if (_service == null || !_connectedAddress.HasValue || !_connectedAt.HasValue)
        {
            MessageBox.Show(this, "Not connected.", "BLE Serial Terminal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.Equals(row.PresetKey, "rt950-ble-unlock", StringComparison.OrdinalIgnoreCase))
        {
            await QueueRt950UnlockAsync($"COMMAND:{row.Id}");
            return;
        }

        if (row.RequiresRt950Unlock && string.Equals(row.ModuleTag, "RT950", StringComparison.OrdinalIgnoreCase) && !IsRt950DataPathReady)
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_ID={row.Id}");
            AppendSystemLine("REASON=RT950_UNLOCK_REQUIRED");
            MessageBox.Show(this, "RT950 BLE unlock must complete before this RT950 diagnostic command is sent.", "RT950 unlock required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        GattCharacteristic? characteristic = ResolveCommandTarget(row.TargetOverride);
        if (characteristic == null)
        {
            MessageBox.Show(this, $"Command target '{row.TargetOverride}' is not available on the current connection.", "Command route unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool withResponse = ResolveWriteWithResponse(row);
        GattCharacteristicProperties properties = characteristic.CharacteristicProperties;
        bool supported = withResponse ? properties.HasFlag(GattCharacteristicProperties.Write) : properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        if (!supported)
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_ID={row.Id}");
            AppendSystemLine($"UUID={BleUuid.Short(characteristic.Uuid)}");
            AppendSystemLine("REASON=SELECTED_WRITE_TYPE_NOT_SUPPORTED");
            return;
        }

        bool isHex = ResolveHexMode(row);
        TxLineEnding ending = ResolveLineEnding(row);
        byte[] payload;
        try
        {
            payload = TxPayloadBuilder.Build(row.Command, isHex, ending);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid TX data", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (payload.Length == 0)
            return;

        if (_optionalFeatureSettings.Rt950ToolsEnabled && BleUuid.Is(characteristic.Uuid, "FF31") && !Rt950Protocol.IsUnlockFrame(payload))
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_ID={row.Id}");
            AppendSystemLine("UUID=FF31");
            AppendSystemLine("REASON=RT950_FF31_RESERVED_FOR_UNLOCK");
            MessageBox.Show(this, "When RT950 Tools are enabled, FF31 is reserved for the verified one-time BLE unlock frame. Normal data uses FFE1.", "RT950 FF31 safeguard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryReadChunkSize(out int chunkSize))
            return;

        var request = new TxCommandRequest(
            row.Id,
            row.Label,
            BleUuid.Full(_service.Uuid),
            BleUuid.Full(characteristic.Uuid),
            withResponse,
            isHex,
            ending,
            payload.ToArray(),
            _connectedAddress,
            _connectedAt,
            chunkSize,
            row.ModuleTag,
            row.RequiresRt950Unlock);

        AppendSystemLine("TX QUEUED");
        AppendSystemLine($"COMMAND_ID={row.Id}");
        AppendSystemLine($"COMMAND_LABEL={row.Label}");
        AppendSystemLine($"UUID={BleUuid.Short(characteristic.Uuid)}");
        AppendSystemLine($"LEN={payload.Length}");
        SaveTxCommandPreferences();

        Task queued = _manualTxQueue.Enqueue(() => Dispatcher.InvokeAsync(() => ExecuteTxRequestAsync(request), DispatcherPriority.Normal).Task.Unwrap());
        try
        {
            await queued;
        }
        catch (Exception ex)
        {
            AppendSystemLine($"TX QUEUE ERROR COMMAND_ID={row.Id}: {ex.Message}");
        }
    }

    private async Task ExecuteTxRequestAsync(TxCommandRequest request)
    {
        if (!_connectedAddress.HasValue || !_connectedAt.HasValue || request.BluetoothAddress != _connectedAddress || request.ConnectedAt != _connectedAt || _service == null)
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_ID={request.CommandId}");
            AppendSystemLine("REASON=CONNECTION_CONTEXT_CHANGED");
            return;
        }

        if (!Guid.TryParse(request.ServiceUuid, out Guid serviceUuid) || _service.Uuid != serviceUuid || !Guid.TryParse(request.WriteUuid, out Guid writeUuid))
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_ID={request.CommandId}");
            AppendSystemLine("REASON=GATT_ROUTE_CHANGED");
            return;
        }

        GattCharacteristic? characteristic = ResolveCurrentCharacteristic(writeUuid);
        if (characteristic == null)
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_ID={request.CommandId}");
            AppendSystemLine($"UUID={BleUuid.Short(writeUuid)}");
            AppendSystemLine("REASON=CHARACTERISTIC_NOT_AVAILABLE");
            return;
        }

        if (request.RequiresRt950Unlock && string.Equals(request.ModuleTag, "RT950", StringComparison.OrdinalIgnoreCase) && !IsRt950DataPathReady)
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_ID={request.CommandId}");
            AppendSystemLine("REASON=RT950_UNLOCK_STATE_CHANGED");
            return;
        }

        GattCharacteristicProperties p = characteristic.CharacteristicProperties;
        bool supported = request.WithResponse ? p.HasFlag(GattCharacteristicProperties.Write) : p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        if (!supported)
        {
            AppendSystemLine("WRITE START RESULT=REJECTED");
            AppendSystemLine($"COMMAND_ID={request.CommandId}");
            AppendSystemLine("REASON=SELECTED_WRITE_TYPE_NOT_SUPPORTED");
            return;
        }

        GattWriteOption option = request.WithResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        bool allSucceeded = true;
        await _gattOperationGate.WaitAsync();
        try
        {
            for (int offset = 0; offset < request.Payload.Length; offset += request.ChunkSize)
            {
                int count = Math.Min(request.ChunkSize, request.Payload.Length - offset);
                byte[] chunk = new byte[count];
                Buffer.BlockCopy(request.Payload, offset, chunk, 0, count);

                AppendSystemLine("RAW BLE WRITE");
                AppendSystemLine($"COMMAND_ID={request.CommandId}");
                if (!string.IsNullOrWhiteSpace(request.Label)) AppendSystemLine($"COMMAND_LABEL={request.Label}");
                AppendSystemLine($"SERVICE={BleUuid.Short(serviceUuid)}");
                AppendSystemLine($"UUID={BleUuid.Short(characteristic.Uuid)}");
                AppendSystemLine($"TYPE={(request.WithResponse ? "WITH_RESPONSE" : "WITHOUT_RESPONSE")}");
                AppendSystemLine($"LEN={chunk.Length}");
                AppendSystemLine($"HEX={Hex(chunk)}");

                using var writer = new DataWriter();
                writer.WriteBytes(chunk);
                IBuffer buffer = writer.DetachBuffer();
                try
                {
                    var operation = characteristic.WriteValueWithResultAsync(buffer, option);
                    AppendSystemLine("WRITE START RESULT=ACCEPTED");
                    AppendSystemLine($"COMMAND_ID={request.CommandId}");
                    if (!request.WithResponse)
                    {
                        AppendSystemLine("WRITE SUBMITTED NO_RESPONSE");
                        AppendSystemLine($"COMMAND_ID={request.CommandId}");
                        AppendSystemLine($"UUID={BleUuid.Short(characteristic.Uuid)}");
                    }

                    GattWriteResult result = await operation;
                    bool success = result.Status == GattCommunicationStatus.Success;
                    allSucceeded &= success;
                    AppendSystemLine("WRITE COMPLETE");
                    AppendSystemLine($"COMMAND_ID={request.CommandId}");
                    AppendSystemLine($"STATUS={(success ? "SUCCESS" : "FAIL")}");
                    AppendSystemLine($"STATUS_CODE={(int)result.Status}");
                    AppendSystemLine($"PROTOCOL_ERROR={ProtocolText(result.ProtocolError)}");
                    if (!success) break;
                    if (!request.WithResponse) await Task.Delay(10);
                }
                catch (Exception ex)
                {
                    allSucceeded = false;
                    AppendSystemLine("WRITE COMPLETE");
                    AppendSystemLine($"COMMAND_ID={request.CommandId}");
                    AppendSystemLine("STATUS=FAIL");
                    AppendSystemLine("STATUS_CODE=EXCEPTION");
                    AppendSystemLine($"HRESULT=0x{ex.HResult:X8}");
                    AppendSystemLine($"EXCEPTION={ex.Message}");
                    break;
                }
            }
        }
        finally
        {
            _gattOperationGate.Release();
        }

        if (!allSucceeded)
            return;

        Interlocked.Add(ref _txBytes, request.Payload.Length);
        if (LocalEchoCheckBox.IsChecked == true)
            AppendTx(request.Payload);
        UpdateCounters();
        _structuredLogStore.Add(
            LogCategory.TX_RAW,
            $"BLE WRITE command_id={request.CommandId} label={request.Label} len={request.Payload.Length} hex={Hex(request.Payload)}",
            direction: "TX",
            device: CurrentLogDevice(),
            characteristic: BleUuid.Short(characteristic.Uuid),
            data: request.Payload);
    }

    private GattCharacteristic? ResolveCommandTarget(string targetOverride)
    {
        if (string.IsNullOrWhiteSpace(targetOverride) || string.Equals(targetOverride, CommandOverrideValues.UseGlobal, StringComparison.OrdinalIgnoreCase))
            return _writeCharacteristic;
        return CurrentServiceCharacteristics().FirstOrDefault(c => BleUuid.Is(c.Uuid, targetOverride));
    }

    private GattCharacteristic? ResolveCurrentCharacteristic(Guid uuid)
    {
        if (_autoGatt.ServiceCharacteristics.Count > 0)
        {
            GattCharacteristic? cached = _autoGatt.ServiceCharacteristics.FirstOrDefault(c => c.Uuid == uuid);
            if (cached != null) return cached;
        }
        if (_writeCharacteristic?.Uuid == uuid) return _writeCharacteristic;
        if (_notifyCharacteristic?.Uuid == uuid) return _notifyCharacteristic;
        return null;
    }

    private bool ResolveWriteWithResponse(CommandRowData row) => row.WriteTypeOverride switch
    {
        CommandOverrideValues.WithResponse => true,
        CommandOverrideValues.WithoutResponse => false,
        _ => WriteTypeComboBox.SelectedIndex != 1
    };

    private bool ResolveHexMode(CommandRowData row) => row.TxModeOverride switch
    {
        CommandOverrideValues.Hex => true,
        CommandOverrideValues.Text => false,
        _ => TxModeComboBox.SelectedIndex == 1
    };

    private TxLineEnding ResolveLineEnding(CommandRowData row) => row.LineEndingOverride switch
    {
        CommandOverrideValues.None => TxLineEnding.None,
        CommandOverrideValues.Lf => TxLineEnding.Lf,
        CommandOverrideValues.Cr => TxLineEnding.Cr,
        CommandOverrideValues.CrLf => TxLineEnding.CrLf,
        _ => GetSelectedTxLineEnding()
    };

    private TxLineEnding GetSelectedTxLineEnding() => LineEndingComboBox.SelectedIndex switch
    {
        1 => TxLineEnding.Cr,
        2 => TxLineEnding.Lf,
        3 => TxLineEnding.CrLf,
        _ => TxLineEnding.None
    };

    private void UpdateTxSendAvailability()
    {
        if (SendButton == null || RoutingStatusTextBlock == null || WriteTypeComboBox == null)
            return;

        if (_writeCharacteristic == null || _device == null || !_bleConnected)
        {
            SendButton.IsEnabled = false;
            RefreshDynamicCommandSendAvailability();
            return;
        }

        bool withResponse = WriteTypeComboBox.SelectedIndex != 1;
        GattCharacteristicProperties p = _writeCharacteristic.CharacteristicProperties;
        bool supported = withResponse ? p.HasFlag(GattCharacteristicProperties.Write) : p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        SendButton.IsEnabled = supported;
        RoutingStatusTextBlock.Text = supported
            ? $"TX {BleUuid.Short(_writeCharacteristic.Uuid)} / {(withResponse ? "With Response" : "Without Response")}; RX {(_notifyCharacteristic == null ? "-" : BleUuid.Short(_notifyCharacteristic.Uuid))}"
            : $"{BleUuid.Short(_writeCharacteristic.Uuid)} does not support {(withResponse ? "Write With Response" : "Write Without Response")}. Send disabled.";
        RefreshDynamicCommandSendAvailability();
    }

    private void RefreshDynamicCommandSendAvailability()
    {
        foreach (CommandRowUi ui in _commandRowUi.Values)
        {
            GattCharacteristic? target = ResolveCommandTarget(ui.Model.TargetOverride);
            if (!_bleConnected || _device == null || target == null)
            {
                ui.Send.IsEnabled = false;
                continue;
            }
            bool withResponse = ResolveWriteWithResponse(ui.Model);
            GattCharacteristicProperties p = target.CharacteristicProperties;
            ui.Send.IsEnabled = withResponse ? p.HasFlag(GattCharacteristicProperties.Write) : p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        }
    }

    private void ScheduleCommandPreferenceSave()
    {
        if (_commandWorkspaceLoading || _txPreferenceSaveTimer == null)
            return;
        _txPreferenceSaveTimer.Stop();
        _txPreferenceSaveTimer.Start();
    }

    private void TxPreferenceSaveTimer_Tick(object? sender, EventArgs e)
    {
        _txPreferenceSaveTimer?.Stop();
        SaveTxCommandPreferences();
    }

    private void LoadTxCommandPreferences()
    {
        _commandWorkspaceLoading = true;
        try
        {
            _commandRows.Clear();
            _commandWorkspaceSettings = new CommandWorkspaceSettings();
            if (System.IO.File.Exists(TxCommandSettingsFile))
            {
                string json = System.IO.File.ReadAllText(TxCommandSettingsFile);
                try
                {
                    CommandWorkspaceSettings? current = JsonSerializer.Deserialize<CommandWorkspaceSettings>(json);
                    if (current?.Rows != null && current.Rows.Count > 0)
                    {
                        _commandWorkspaceSettings = current;
                        _commandRows.AddRange(current.Rows.Select(r => r.Clone()));
                    }
                    else
                    {
                        TxCommandPreferencesData? legacy = JsonSerializer.Deserialize<TxCommandPreferencesData>(json);
                        if (legacy?.Slots != null)
                        {
                            int requested = Math.Max(1, legacy.RowCount);
                            foreach (TxCommandSlotData slot in legacy.Slots.OrderBy(s => s.Slot).Take(requested))
                            {
                                _commandRows.Add(new CommandRowData
                                {
                                    Label = string.IsNullOrWhiteSpace(slot.Label) ? $"Command {slot.Slot}" : slot.Label,
                                    Command = slot.Command ?? string.Empty
                                });
                            }
                        }
                    }
                }
                catch
                {
                    _commandWorkspaceSettings = new CommandWorkspaceSettings();
                }
            }

            if (_commandRows.Count == 0)
                _commandRows.Add(NewGeneralCommand("Command 1"));
        }
        catch
        {
            _commandRows.Clear();
            _commandRows.Add(NewGeneralCommand("Command 1"));
            _commandWorkspaceSettings = new CommandWorkspaceSettings();
        }
        finally
        {
            _commandWorkspaceLoading = false;
        }
    }

    private void SaveTxCommandPreferences()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsDirectory);
            _commandWorkspaceSettings.Rows = _commandRows.Select(r => r.Clone()).ToList();
            string json = JsonSerializer.Serialize(_commandWorkspaceSettings, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(TxCommandSettingsFile, json, new UTF8Encoding(false));
        }
        catch
        {
            // Command persistence is diagnostic convenience and must never interfere with BLE operation.
        }
    }

    internal void SetCommandRemoveConfirmation(bool enabled)
    {
        _commandWorkspaceSettings.ConfirmRemove = enabled;
        SaveTxCommandPreferences();
    }

    internal bool CommandRemoveConfirmationEnabled => _commandWorkspaceSettings.ConfirmRemove;

    internal void AddRt950PresetCommands()
    {
        var presets = new[]
        {
            new CommandRowData
            {
                Label = "BLE Unlock",
                Command = Hex(Rt950Protocol.UnlockFrame),
                TargetOverride = "FF31",
                WriteTypeOverride = CommandOverrideValues.WithResponse,
                TxModeOverride = CommandOverrideValues.Hex,
                LineEndingOverride = CommandOverrideValues.None,
                ModuleTag = "RT950",
                PresetKey = "rt950-ble-unlock"
            },
            new CommandRowData
            {
                Label = "OEM Handshake",
                Command = Hex(Rt950Protocol.OemHandshake),
                TargetOverride = "FFE1",
                WriteTypeOverride = CommandOverrideValues.WithResponse,
                TxModeOverride = CommandOverrideValues.Hex,
                LineEndingOverride = CommandOverrideValues.None,
                ModuleTag = "RT950",
                PresetKey = "rt950-oem-handshake",
                RequiresRt950Unlock = true
            },
            new CommandRowData
            {
                Label = "Model Query",
                Command = Hex(Rt950Protocol.ModelQuery),
                TargetOverride = "FFE1",
                WriteTypeOverride = CommandOverrideValues.WithResponse,
                TxModeOverride = CommandOverrideValues.Hex,
                LineEndingOverride = CommandOverrideValues.None,
                ModuleTag = "RT950",
                PresetKey = "rt950-model-query",
                RequiresRt950Unlock = true
            }
        };

        int added = 0;
        foreach (CommandRowData preset in presets)
        {
            if (_commandRows.Any(r => string.Equals(r.PresetKey, preset.PresetKey, StringComparison.OrdinalIgnoreCase)))
                continue;
            _commandRows.Add(preset);
            added++;
        }

        RebuildDynamicCommandRows();
        SaveTxCommandPreferences();
        AppendSystemLine($"RT950 TEST COMMANDS LOADED added={added}; DATA_SENT=NO");
    }

    // Compatibility handlers remain because the legacy static XAML command controls are created during InitializeComponent,
    // then replaced by the dynamic unbounded workspace in OnInitialized.
    private async void Rt950OemPresetButton_Click(object sender, RoutedEventArgs e) => await ApplyRt950GattPresetAsync();
    private async void Rt950Ffe1PresetButton_Click(object sender, RoutedEventArgs e) => await ApplyRt950GattPresetAsync();
    private void LoadRt950TestCommandsButton_Click(object sender, RoutedEventArgs e) => AddRt950PresetCommands();
    private void TxCommandRowCountMenuItem_Click(object sender, RoutedEventArgs e) => SetStatus("Command rows are unlimited. Use + ADD COMMAND or X to add/remove rows.");
    private void CommandPreferenceTextChanged(object sender, TextChangedEventArgs e) { }

    private async void SendCommandSlotButton_Click(object sender, RoutedEventArgs e)
    {
        int slot = sender is FrameworkElement element && int.TryParse(element.Tag?.ToString(), out int value) ? value : 1;
        if (slot >= 1 && slot <= _commandRows.Count)
            await QueueCommandRowAsync(_commandRows[slot - 1]);
    }

    private async void CommandInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        int slot = sender is FrameworkElement element && int.TryParse(element.Tag?.ToString(), out int value) ? value : 1;
        if (slot >= 1 && slot <= _commandRows.Count)
            await QueueCommandRowAsync(_commandRows[slot - 1]);
    }
}
