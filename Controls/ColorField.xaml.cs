using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace SnapAnchor.Controls;

public partial class ColorField : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(ColorField),
        new FrameworkPropertyMetadata("#FF0067C0", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (owner, _) => ((ColorField)owner).TextUpdated()));
    internal event EventHandler? ValueChanged;
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public ColorField() { InitializeComponent(); UpdateSwatch(); }
    internal void FocusValue() { ValueBox.Focus(); ValueBox.SelectAll(); }
    private void TextUpdated() { UpdateSwatch(); ValueChanged?.Invoke(this, EventArgs.Empty); }

    internal static bool TryParse(string? text, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { color = (Color)ColorConverter.ConvertFromString(text.Trim()); return true; }
        catch { return false; }
    }

    private void UpdateSwatch()
    {
        if (Swatch is null) return;
        Swatch.Background = TryParse(Text, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var valid = TryParse(Text, out var previous);
        using var dialog = new Forms.ColorDialog { FullOpen = true, Color = valid ? System.Drawing.Color.FromArgb(previous.R, previous.G, previous.B) : System.Drawing.Color.DodgerBlue };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        Text = Color.FromArgb(valid ? previous.A : (byte)255, dialog.Color.R, dialog.Color.G, dialog.Color.B).ToString();
    }
}
