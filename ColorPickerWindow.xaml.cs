using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BLESerialTerminal;

public partial class ColorPickerWindow : Window
{
    public string SelectedColorHex { get; private set; }

    public ColorPickerWindow(string title, string initialColor)
    {
        InitializeComponent();
        Title = title;
        PromptTextBlock.Text = title;
        SelectedColorHex = NormalizeOrDefault(initialColor);
        HexTextBox.Text = SelectedColorHex;
        UpdatePreview();
    }

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string color)
            HexTextBox.Text = color;
    }

    private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (!TryNormalize(HexTextBox.Text, out string normalized))
        {
            ValidationTextBlock.Text = "Use #RRGGBB or #AARRGGBB";
            OkButton.IsEnabled = false;
            return;
        }

        ValidationTextBlock.Text = string.Empty;
        OkButton.IsEnabled = true;
        PreviewBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(normalized));
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalize(HexTextBox.Text, out string normalized))
            return;

        SelectedColorHex = normalized;
        DialogResult = true;
    }

    private static string NormalizeOrDefault(string value) =>
        TryNormalize(value, out string normalized) ? normalized : "#000000";

    private static bool TryNormalize(string value, out string normalized)
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
}
