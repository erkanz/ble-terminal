using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BLESerialTerminal;

public partial class MainWindow
{
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (TryBlockManualTxBeforeNotifyReady(e.OriginalSource as DependencyObject, key: null))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if ((e.Key == Key.Enter || e.Key == Key.Space) &&
            TryBlockManualTxBeforeNotifyReady(e.OriginalSource as DependencyObject, e.Key))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private bool TryBlockManualTxBeforeNotifyReady(DependencyObject? source, Key? key)
    {
        if (!_autoGatt.IsRadtelRt950Kiss || _ffe1CccdEnabled)
            return false;

        int? slot = ResolveTxSlotFromInputSource(source, key);
        if (!slot.HasValue)
            return false;

        AppendSystemLine("WRITE START RESULT=REJECTED");
        AppendSystemLine($"COMMAND_SLOT={slot.Value}");
        AppendSystemLine("REASON=FFE1_CCCD_NOT_READY");
        SetStatus("RT950 TX blocked until FFE1 Notify/CCCD is ready.");
        return true;
    }

    private static int? ResolveTxSlotFromInputSource(DependencyObject? source, Key? key)
    {
        if (source == null)
            return null;

        if (FindAncestor<Button>(source) is Button button &&
            int.TryParse(button.Tag?.ToString(), out int buttonSlot) &&
            buttonSlot is >= 1 and <= 5)
        {
            return buttonSlot;
        }

        // Enter in a command TextBox is a send gesture. Space inside a TextBox is normal input.
        if (key == Key.Enter && FindAncestor<TextBox>(source) is TextBox textBox &&
            int.TryParse(textBox.Tag?.ToString(), out int textSlot) &&
            textSlot is >= 1 and <= 5)
        {
            return textSlot;
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current != null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
