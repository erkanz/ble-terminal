using System.Text;
using System.Windows;
using System.Windows.Controls;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BLESerialTerminal;

public partial class GattInspectorWindow
{
    private bool _bleInfoButtonsAdded;

    private static readonly (string Uuid, string Label, bool Text)[] DeviceInfoFields =
    {
        ("2A23", "System ID", false),
        ("2A24", "Model Number", true),
        ("2A25", "Serial Number", true),
        ("2A26", "Firmware Revision", true),
        ("2A27", "Hardware Revision", true),
        ("2A28", "Software Revision", true),
        ("2A29", "Manufacturer Name", true),
        ("2A2A", "IEEE 11073 Certification Data", false),
        ("2A50", "PnP ID", false)
    };

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_bleInfoButtonsAdded)
            return;

        _bleInfoButtonsAdded = true;

        if (RefreshButton.Parent is not StackPanel buttonPanel)
            return;

        var readBleNameButton = new Button
        {
            Content = "Read BLE Name",
            Width = 115,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Read Generic Access Device Name (1800 / 2A00). Read-only; no setting is changed."
        };
        readBleNameButton.Click += ReadBleNameButton_Click;

        var readModuleInfoButton = new Button
        {
            Content = "Read Module Info",
            Width = 125,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Read Device Information Service (180A). Read-only; no setting is changed."
        };
        readModuleInfoButton.Click += ReadModuleInfoButton_Click;

        var readGattLabelsButton = new Button
        {
            Content = "Read GATT Labels",
            Width = 125,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Read Characteristic User Description descriptors (2901). Read-only; no setting is changed."
        };
        readGattLabelsButton.Click += ReadGattLabelsButton_Click;

        buttonPanel.Children.Insert(0, readGattLabelsButton);
        buttonPanel.Children.Insert(0, readModuleInfoButton);
        buttonPanel.Children.Insert(0, readBleNameButton);
    }

    private async void ReadBleNameButton_Click(object sender, RoutedEventArgs e)
    {
        Button? sourceButton = sender as Button;
        if (sourceButton != null)
            sourceButton.IsEnabled = false;

        await _gattOperationGate.WaitAsync();
        try
        {
            GattCharacteristicInfo? nameInfo = FindCharacteristic("1800", "2A00");

            if (nameInfo == null)
            {
                Log("*** BLE DEVICE NAME READ FAILED");
                Log("SERVICE=1800 CHARACTERISTIC=2A00 NOT FOUND");
                return;
            }

            GattCharacteristic characteristic = nameInfo.Characteristic;
            if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
            {
                Log("*** BLE DEVICE NAME READ FAILED");
                Log("SERVICE=1800 UUID=2A00 READ PROPERTY NOT AVAILABLE");
                return;
            }

            Log("*** BLE DEVICE NAME READ START");
            Log("SERVICE=1800");
            Log("UUID=2A00");

            GattReadResult result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            Log($"onCharacteristicRead uuid=2A00 {StatusText(result.Status, result.ProtocolError)}");

            if (result.Status != GattCommunicationStatus.Success || result.Value == null)
            {
                Log($"*** BLE DEVICE NAME READ FAILED {StatusText(result.Status, result.ProtocolError)}");
                return;
            }

            byte[] data = BufferToBytes(result.Value);
            string name = Encoding.UTF8.GetString(data).TrimEnd('\0');

            Interlocked.Add(ref _rxBytes, data.Length);
            _dataReceived = true;

            Log("*** BLE DEVICE NAME READ PASS");
            Log($"NAME={name}");
            Log($"HEX={Hex(data)}");
            UpdateStatePanel();

            MessageBox.Show(
                this,
                string.IsNullOrEmpty(name) ? "Device Name is empty." : name,
                "BLE Device Name (1800 / 2A00)",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"*** BLE DEVICE NAME READ ERROR exception={ex.Message}");
        }
        finally
        {
            _gattOperationGate.Release();
            if (sourceButton != null)
                sourceButton.IsEnabled = true;
        }
    }

    private async void ReadModuleInfoButton_Click(object sender, RoutedEventArgs e)
    {
        Button? sourceButton = sender as Button;
        if (sourceButton != null)
            sourceButton.IsEnabled = false;

        await _gattOperationGate.WaitAsync();
        try
        {
            bool servicePresent = _services.Any(service => BleUuid.Is(service.Service.Uuid, "180A"));
            if (!servicePresent)
            {
                Log("*** BLE MODULE INFO READ FAILED");
                Log("SERVICE=180A NOT FOUND");
                return;
            }

            Log("*** BLE MODULE INFO READ START");
            Log("SERVICE=180A");

            var summary = new StringBuilder();
            int successCount = 0;

            foreach ((string uuid, string label, bool isText) in DeviceInfoFields)
            {
                GattCharacteristicInfo? info = FindCharacteristic("180A", uuid);
                if (info == null)
                {
                    Log($"{label} [{uuid}]: NOT FOUND");
                    summary.AppendLine($"{label}: not exposed");
                    continue;
                }

                GattCharacteristic characteristic = info.Characteristic;
                if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                {
                    Log($"{label} [{uuid}]: READ PROPERTY NOT AVAILABLE");
                    summary.AppendLine($"{label}: not readable");
                    continue;
                }

                try
                {
                    GattReadResult result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                    Log($"onCharacteristicRead uuid={uuid} {StatusText(result.Status, result.ProtocolError)}");

                    if (result.Status != GattCommunicationStatus.Success || result.Value == null)
                    {
                        Log($"{label} [{uuid}]: READ FAILED {StatusText(result.Status, result.ProtocolError)}");
                        summary.AppendLine($"{label}: read failed ({result.Status})");
                        continue;
                    }

                    byte[] data = BufferToBytes(result.Value);
                    Interlocked.Add(ref _rxBytes, data.Length);
                    _dataReceived = true;
                    successCount++;

                    if (isText)
                    {
                        string text = Encoding.UTF8.GetString(data).TrimEnd('\0');
                        Log($"{label} [{uuid}]: {text}");
                        Log($"{label} HEX={Hex(data)}");
                        summary.AppendLine($"{label}: {(string.IsNullOrEmpty(text) ? "(empty)" : text)}");
                    }
                    else
                    {
                        string decoded = DecodeDeviceInfoBinary(uuid, data);
                        Log($"{label} [{uuid}]: {decoded}");
                        Log($"{label} HEX={Hex(data)}");
                        summary.AppendLine($"{label}: {decoded}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"{label} [{uuid}]: READ ERROR exception={ex.Message}");
                    summary.AppendLine($"{label}: read error");
                }
            }

            Log($"*** BLE MODULE INFO READ COMPLETE success={successCount}/{DeviceInfoFields.Length}");
            UpdateStatePanel();

            MessageBox.Show(
                this,
                summary.Length == 0 ? "No Device Information fields were readable." : summary.ToString().TrimEnd(),
                "BLE Module Info (180A)",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"*** BLE MODULE INFO READ ERROR exception={ex.Message}");
        }
        finally
        {
            _gattOperationGate.Release();
            if (sourceButton != null)
                sourceButton.IsEnabled = true;
        }
    }

    private async void ReadGattLabelsButton_Click(object sender, RoutedEventArgs e)
    {
        Button? sourceButton = sender as Button;
        if (sourceButton != null)
            sourceButton.IsEnabled = false;

        await _gattOperationGate.WaitAsync();
        try
        {
            Log("*** GATT USER DESCRIPTION READ START");
            int found = 0;
            int success = 0;
            var summary = new StringBuilder();

            foreach (GattServiceInfo service in _services)
            {
                foreach (GattCharacteristicInfo characteristicInfo in service.Characteristics)
                {
                    foreach (GattDescriptor descriptor in characteristicInfo.Descriptors.Where(d => BleUuid.Is(d.Uuid, "2901")))
                    {
                        found++;
                        string serviceUuid = BleUuid.Short(service.Service.Uuid);
                        string characteristicUuid = BleUuid.Short(characteristicInfo.Characteristic.Uuid);

                        try
                        {
                            GattReadResult result = await descriptor.ReadValueAsync(BluetoothCacheMode.Uncached);
                            Log($"onDescriptorRead uuid=2901 service={serviceUuid} char={characteristicUuid} {StatusText(result.Status, result.ProtocolError)}");

                            if (result.Status != GattCommunicationStatus.Success || result.Value == null)
                            {
                                Log($"LABEL service={serviceUuid} char={characteristicUuid}: READ FAILED {StatusText(result.Status, result.ProtocolError)}");
                                summary.AppendLine($"{serviceUuid}/{characteristicUuid}: read failed");
                                continue;
                            }

                            byte[] data = BufferToBytes(result.Value);
                            string text = Encoding.UTF8.GetString(data).TrimEnd('\0');
                            Interlocked.Add(ref _rxBytes, data.Length);
                            _dataReceived = true;
                            success++;

                            Log($"LABEL service={serviceUuid} char={characteristicUuid}: {text}");
                            Log($"LABEL HEX={Hex(data)}");
                            summary.AppendLine($"{serviceUuid}/{characteristicUuid}: {(string.IsNullOrEmpty(text) ? "(empty)" : text)}");
                        }
                        catch (Exception ex)
                        {
                            Log($"LABEL service={serviceUuid} char={characteristicUuid}: READ ERROR exception={ex.Message}");
                            summary.AppendLine($"{serviceUuid}/{characteristicUuid}: read error");
                        }
                    }
                }
            }

            Log($"*** GATT USER DESCRIPTION READ COMPLETE success={success}/{found}");
            UpdateStatePanel();

            MessageBox.Show(
                this,
                found == 0 ? "No 2901 Characteristic User Description descriptors were discovered." : summary.ToString().TrimEnd(),
                "GATT Labels (2901)",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"*** GATT USER DESCRIPTION READ ERROR exception={ex.Message}");
        }
        finally
        {
            _gattOperationGate.Release();
            if (sourceButton != null)
                sourceButton.IsEnabled = true;
        }
    }

    private GattCharacteristicInfo? FindCharacteristic(string serviceUuid, string characteristicUuid)
    {
        return _services
            .Where(service => BleUuid.Is(service.Service.Uuid, serviceUuid))
            .SelectMany(service => service.Characteristics)
            .FirstOrDefault(characteristic => BleUuid.Is(characteristic.Characteristic.Uuid, characteristicUuid));
    }

    private static string DecodeDeviceInfoBinary(string uuid, byte[] data)
    {
        if (BleUuid.Is(Guid.Parse("0000" + uuid + "-0000-1000-8000-00805F9B34FB"), "2A50") && data.Length >= 7)
        {
            ushort vendorId = (ushort)(data[1] | (data[2] << 8));
            ushort productId = (ushort)(data[3] | (data[4] << 8));
            ushort productVersion = (ushort)(data[5] | (data[6] << 8));
            return $"source=0x{data[0]:X2}, vendor=0x{vendorId:X4}, product=0x{productId:X4}, version=0x{productVersion:X4}";
        }

        return Hex(data);
    }
}
