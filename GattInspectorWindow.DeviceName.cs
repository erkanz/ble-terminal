using System.Text;
using System.Windows;
using System.Windows.Controls;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BLESerialTerminal;

public partial class GattInspectorWindow
{
    private bool _bleNameButtonAdded;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_bleNameButtonAdded)
            return;

        _bleNameButtonAdded = true;

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
        buttonPanel.Children.Insert(0, readBleNameButton);
    }

    private async void ReadBleNameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            button.IsEnabled = false;

        await _gattOperationGate.WaitAsync();
        try
        {
            GattCharacteristicInfo? nameInfo = _services
                .Where(service => BleUuid.Is(service.Service.Uuid, "1800"))
                .SelectMany(service => service.Characteristics)
                .FirstOrDefault(characteristic => BleUuid.Is(characteristic.Characteristic.Uuid, "2A00"));

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
            if (sender is Button button)
                button.IsEnabled = true;
        }
    }
}
