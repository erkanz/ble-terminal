using System.Windows;

namespace BLESerialTerminal;

public partial class MainWindow
{
    private bool TryReadChunkSize(out int chunkSize)
    {
        chunkSize = 20;
        if (!int.TryParse(ChunkSizeTextBox.Text.Trim(), out chunkSize) || chunkSize < 1 || chunkSize > 512)
        {
            MessageBox.Show(this,
                "TX chunk bytes must be from 1 to 512.",
                "BLE Serial Terminal",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        return true;
    }
}
