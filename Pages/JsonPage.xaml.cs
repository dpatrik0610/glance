using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glance.Services;

namespace Glance.Pages;

public partial class JsonPage : UserControl
{
    JsonText.Result result = new(false, "");

    public JsonPage()
    {
        InitializeComponent();
        Changed();
    }

    void Changed(object? sender = null, TextChangedEventArgs? e = null)
    {
        result = JsonText.Check(Box.Text);
        var empty = result.Message == "";
        StatusIcon.Text = empty ? "" : result.Ok ? "" : "";
        StatusIcon.Foreground = (Brush)FindResource(result.Ok ? "Green" : "Red");
        StatusText.Text = empty ? "Checked as you type" : result.Ok ? $"Valid JSON · {result.Message}" : result.Message;
        Status.Cursor = result.ErrorAt >= 0 ? Cursors.Hand : null;
        Status.ToolTip = result.ErrorAt >= 0 ? "Show where" : null;
        FormatButton.IsEnabled = MinifyButton.IsEnabled = result.Ok;
        CopyButton.IsEnabled = !empty;
    }

    void Format(object sender, RoutedEventArgs e) => Replace(JsonText.Format(Box.Text, indent: true));

    void Minify(object sender, RoutedEventArgs e) => Replace(JsonText.Format(Box.Text, indent: false));

    // Through the selection rather than Text, so Ctrl+Z brings the original back.
    void Replace(string text)
    {
        Box.SelectAll();
        Box.SelectedText = text.ReplaceLineEndings();
        Box.CaretIndex = 0;
        Box.ScrollToHome();
        Box.Focus();
    }

    void Copy(object sender, RoutedEventArgs e) => ClipboardHistory.Copy(Box.Text);

    void GoToError(object sender, MouseButtonEventArgs e)
    {
        if (result.ErrorAt < 0) return;
        Box.Focus();
        Box.CaretIndex = result.ErrorAt;
        Box.ScrollToLine(Box.GetLineIndexFromCharacterIndex(result.ErrorAt));
    }
}
