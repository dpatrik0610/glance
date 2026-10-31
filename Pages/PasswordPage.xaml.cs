using System.Windows;
using System.Windows.Controls;
using Glance.Services;

namespace Glance.Pages;

public partial class PasswordPage : UserControl
{
    static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);

    public PasswordPage()
    {
        InitializeComponent();
        Generate();
    }

    void Generate(object? sender = null, RoutedEventArgs? e = null)
    {
        if (SimilarBox is null) return; // slider fires during InitializeComponent, before the switches exist
        var length = (int)Length.Value;
        LengthText.Text = length.ToString();
        var (pwd, bits) = Passwords.Generate(length, UpperBox.IsChecked == true, LowerBox.IsChecked == true,
            DigitBox.IsChecked == true, SymbolBox.IsChecked == true, SimilarBox.IsChecked == true);
        Output.Text = pwd;
        Strength.Value = Math.Min(bits, Strength.Maximum);
        (Strength.Foreground, StrengthText.Text) = bits switch
        {
            < 50 => ((System.Windows.Media.Brush)FindResource("Red"), $"{bits:0} bits · weak"),
            < 80 => ((System.Windows.Media.Brush)FindResource("Amber"), $"{bits:0} bits · okay"),
            _ => ((System.Windows.Media.Brush)FindResource("Green"), $"{bits:0} bits · strong"),
        };
        Status.Text = "";
    }

    void Copy(object sender, RoutedEventArgs e) =>
        Status.Text = ClipboardHistory.CopySecret(Output.Text, ClearAfter)
            ? $"Copied. Clipboard clears in {ClearAfter.TotalSeconds:0}s; skipped by clipboard history."
            : "Clipboard busy, try again.";
}
