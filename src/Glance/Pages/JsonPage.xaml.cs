using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
        StatusIcon.Foreground = Skin.Brush(result.Ok ? "Green" : "Red");
        StatusText.Text = empty ? "Checked as you type" : result.Ok ? $"Valid JSON · {result.Message}" : result.Message;
        Status.Cursor = result.ErrorAt >= 0 ? Cursors.Hand : null;
        Status.ToolTip = result.ErrorAt >= 0 ? "Show where" : null;
        GoButton.Visibility = result.ErrorAt >= 0 ? Visibility.Visible : Visibility.Collapsed;
        FormatButton.IsEnabled = MinifyButton.IsEnabled = result.Ok;
        CopyButton.IsEnabled = !empty;
        // After layout, so the box's line positions are current
        Dispatcher.InvokeAsync(Place, DispatcherPriority.Loaded);
    }

    void Scrolled(object sender, ScrollChangedEventArgs e) => Place();

    // Numbers the visible lines and marks the error line and character, from where the box drew them.
    internal void Place()
    {
        int first = Box.GetFirstVisibleLineIndex(), last = Box.GetLastVisibleLineIndex();
        ErrorLine.Visibility = ErrorMark.Visibility = Visibility.Collapsed;
        Gutter.Inlines.Clear();
        if (first < 0 || last < first) return;

        var top = Box.GetRectFromCharacterIndex(Box.GetCharacterIndexFromLineIndex(first));
        if (top.IsEmpty) return;
        var errLine = result.ErrorAt >= 0 ? Box.GetLineIndexFromCharacterIndex(result.ErrorAt) : -1;
        for (var i = first; i <= last; i++)
        {
            var n = new Run((i + 1).ToString());
            if (i == errLine) { n.Foreground = Skin.Brush("Red"); n.FontWeight = FontWeights.SemiBold; }
            if (i > first) Gutter.Inlines.Add(new LineBreak());
            Gutter.Inlines.Add(n);
        }
        Gutter.LineHeight = top.Height;
        Canvas.SetTop(Gutter, Box.TranslatePoint(top.TopLeft, Overlay).Y);

        if (errLine < first || errLine > last) return;
        var at = Box.GetRectFromCharacterIndex(result.ErrorAt);
        var end = Box.GetRectFromCharacterIndex(result.ErrorAt, true);
        if (at.IsEmpty) return;
        var p = Box.TranslatePoint(at.TopLeft, Overlay);
        Canvas.SetTop(ErrorLine, p.Y);
        ErrorLine.Height = at.Height;
        ErrorLine.Width = Overlay.ActualWidth;
        ErrorLine.Visibility = Visibility.Visible;
        // Under the character the parser stopped at; a short dash at a line end. Hidden when scrolled under the gutter.
        if (p.X < Box.TranslatePoint(default, Overlay).X) return;
        Canvas.SetLeft(ErrorMark, p.X);
        Canvas.SetTop(ErrorMark, p.Y + at.Height - 2);
        ErrorMark.Width = end.IsEmpty || end.Left <= at.Left ? 8 : end.Left - at.Left;
        ErrorMark.Visibility = Visibility.Visible;
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

    void GoToError(object sender, RoutedEventArgs e)
    {
        if (result.ErrorAt < 0) return;
        Box.Focus();
        Box.CaretIndex = result.ErrorAt;
        Box.ScrollToLine(Box.GetLineIndexFromCharacterIndex(result.ErrorAt));
        // ScrollToLine is vertical only; minified JSON is one long line, so bring the column into view too
        Box.UpdateLayout();
        var x = Box.GetRectFromCharacterIndex(result.ErrorAt).Left;
        if (x < Box.Padding.Left * 2 || x > Box.ActualWidth - 24)
            Box.ScrollToHorizontalOffset(Math.Max(0, Box.HorizontalOffset + x - Box.ActualWidth / 2));
    }
}
