using System.Windows;
using System.Windows.Controls;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private MultiDeviceCompareWindow? _multiDeviceCompareWindow;
    private MenuItem? _multiDeviceCompareMenuItem;

    private void InitializeMultiDeviceCompare()
    {
        InstallMultiDeviceCompareMenuItem();
    }

    private void ShutdownMultiDeviceCompare()
    {
        try { _multiDeviceCompareWindow?.Close(); } catch { }
        _multiDeviceCompareWindow = null;

        if (_multiDeviceCompareMenuItem != null)
        {
            _multiDeviceCompareMenuItem.Click -= MultiDeviceCompareMenuItem_Click;
            _multiDeviceCompareMenuItem = null;
        }
    }

    private void InstallMultiDeviceCompareMenuItem()
    {
        if (Content is not DockPanel dock)
            return;

        Menu? menu = dock.Children.OfType<Menu>().FirstOrDefault();
        MenuItem? view = menu?.Items.OfType<MenuItem>()
            .FirstOrDefault(item => (item.Header?.ToString() ?? string.Empty)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Equals("View", StringComparison.OrdinalIgnoreCase));
        if (view == null)
            return;

        if (view.Items.OfType<MenuItem>().Any(item =>
                string.Equals(item.Header?.ToString(), "Multi-Device Live Compare...", StringComparison.Ordinal)))
            return;

        view.Items.Add(new Separator());
        _multiDeviceCompareMenuItem = new MenuItem { Header = "Multi-Device Live Compare..." };
        _multiDeviceCompareMenuItem.Click += MultiDeviceCompareMenuItem_Click;
        view.Items.Add(_multiDeviceCompareMenuItem);
    }

    private void MultiDeviceCompareMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_multiDeviceCompareWindow is { IsLoaded: true })
        {
            _multiDeviceCompareWindow.Activate();
            return;
        }

        _multiDeviceCompareWindow = new MultiDeviceCompareWindow
        {
            Owner = this
        };
        _multiDeviceCompareWindow.Closed += (_, _) => _multiDeviceCompareWindow = null;
        _multiDeviceCompareWindow.Show();
    }
}
