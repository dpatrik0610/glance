using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Glance.Services;
using Microsoft.Win32;

namespace Glance.Pages;

/// Convert, crop and resize one image. Crop and size are in the picture's own pixels; nothing is written until Save.
public partial class ImagePage : UserControl
{
    enum Drag { None, New, Corner, Move }

    BitmapSource? src;
    string? path;
    Rect crop;                      // in source pixels
    double? ratio;                  // width / height, null = free
    double scaleX = 1, scaleY = 1;  // output size relative to the crop, kept when the crop changes
    ImageOps.Format format = ImageOps.Formats[0];
    Drag drag;
    Point anchor;                   // New/Corner: the fixed corner; Move: where in the frame it was grabbed
    readonly Rectangle[] handles = new Rectangle[4];
    readonly DispatcherTimer estimateSoon;
    int estimateId;

    int W => src!.PixelWidth;
    int H => src!.PixelHeight;
    int OutW => Math.Max(1, (int)Math.Round(crop.Width * scaleX));
    int OutH => Math.Max(1, (int)Math.Round(crop.Height * scaleY));

    public ImagePage()
    {
        InitializeComponent();
        for (var i = 0; i < 4; i++)
            Overlay.Children.Add(handles[i] = new Rectangle { Width = 9, Height = 9, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 0.5 });

        foreach (var (label, r) in new (string, double?)[] { ("Free", null), ("1:1", 1), ("4:3", 4.0 / 3), ("3:4", 3.0 / 4), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16) })
        {
            var chip = Chip(label, "ratio", r is null);
            chip.Checked += (_, _) => SetRatio(r);
            Ratios.Children.Add(chip);
        }
        foreach (var pct in new[] { 25, 50, 75, 100 })
        {
            var b = new Button { Content = pct + "%", Style = Skin.Style("Ghost"), Margin = new(0, 0, 6, 0) };
            b.Click += (_, _) => { scaleX = scaleY = pct / 100.0; Changed(); };
            Scales.Children.Add(b);
        }
        foreach (var f in ImageOps.Formats)
        {
            var chip = Chip(f.Name, "format", f == format);
            chip.Checked += (_, _) => { format = f; Changed(); };
            FormatChips.Children.Add(chip);
        }
        estimateSoon = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => Estimate(), Dispatcher);
        Show();
    }

    RadioButton Chip(string text, string group, bool on) =>
        new() { Content = text, GroupName = group, IsChecked = on, Style = Skin.Style("Chip"), Margin = new(0, 0, 6, 6) };

    // ---- Opening ----

    void OpenClick(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = ImageOps.OpenFilter };
        if (d.ShowDialog() == true) Open(d.FileName);
    }

    void DraggedOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Dropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) Open(files[0]);
    }

    async void Open(string file)
    {
        Status.Text = "Opening…";
        try { src = await Task.Run(() => ImageOps.Load(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException
                                      or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Status.Text = $"Can't open {System.IO.Path.GetFileName(file)}: not an image Windows can read.";
            return;
        }
        path = file;
        crop = new Rect(0, 0, W, H);
        scaleX = scaleY = 1;
        // Start in the format it already is, if that's one we write.
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant().Replace(".jpeg", ".jpg").Replace(".tiff", ".tif");
        foreach (RadioButton chip in FormatChips.Children)
            if (ImageOps.Formats.First(f => f.Name == (string)chip.Content).Ext == ext) chip.IsChecked = true;
        Status.Text = "";
        FileText.Text = $"{System.IO.Path.GetFileName(file)} · {W} × {H} · {Units.Bytes(new FileInfo(file).Length)}";
        Picture.Source = src;
        Show();
    }

    // ---- Crop frame ----

    void SetRatio(double? r)
    {
        ratio = r;
        if (src is null || r is not { } k) return;
        // The biggest frame of that shape, centered where the current one is.
        double w = W, h = W / k;
        if (h > H) (w, h) = (H * k, H);
        var c = new Point(crop.X + crop.Width / 2, crop.Y + crop.Height / 2);
        crop = new Rect(Math.Clamp(c.X - w / 2, 0, W - w), Math.Clamp(c.Y - h / 2, 0, H - h), w, h);
        Changed();
    }

    void ResetCrop(object sender, RoutedEventArgs e)
    {
        if (src is null) return;
        foreach (RadioButton chip in Ratios.Children) chip.IsChecked = (string)chip.Content == "Free";
        crop = new Rect(0, 0, W, H);
        Changed();
    }

    // Where the picture sits in the stage (Stretch=Uniform), and the conversions both ways.
    (double S, double X, double Y) Fit()
    {
        var s = Math.Min(Stage.ActualWidth / W, Stage.ActualHeight / H);
        return (s, (Stage.ActualWidth - W * s) / 2, (Stage.ActualHeight - H * s) / 2);
    }

    Point ToImage(Point p)
    {
        var (s, x, y) = Fit();
        return new(Math.Clamp((p.X - x) / s, 0, W), Math.Clamp((p.Y - y) / s, 0, H));
    }

    Rect ToStage(Rect r)
    {
        var (s, x, y) = Fit();
        return new(x + r.X * s, y + r.Y * s, r.Width * s, r.Height * s);
    }

    static Point[] Corners(Rect r) => [r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft];

    void StageDown(object sender, MouseButtonEventArgs e)
    {
        if (src is null) return;
        var p = e.GetPosition(Stage);
        var frame = ToStage(crop);
        var corners = Corners(frame);
        var hit = Array.FindIndex(corners, c => (c - p).Length < 12);
        if (hit >= 0) (drag, anchor) = (Drag.Corner, Corners(crop)[(hit + 2) % 4]);
        else if (frame.Contains(p) && crop.Size != new Size(W, H)) (drag, anchor) = (Drag.Move, (Point)(ToImage(p) - crop.TopLeft));
        else (drag, anchor) = (Drag.New, ToImage(p));
        Stage.CaptureMouse();
    }

    void StageMove(object sender, MouseEventArgs e)
    {
        if (src is null) return;
        var p = e.GetPosition(Stage);
        if (drag == Drag.None)
        {
            var frame = ToStage(crop);
            var near = Array.FindIndex(Corners(frame), c => (c - p).Length < 12);
            Stage.Cursor = near is 0 or 2 ? Cursors.SizeNWSE : near is 1 or 3 ? Cursors.SizeNESW
                : frame.Contains(p) && crop.Size != new Size(W, H) ? Cursors.SizeAll : Cursors.Cross;
            return;
        }
        var ip = ToImage(p);
        if (drag == Drag.Move)
            crop = new Rect(Math.Clamp(ip.X - anchor.X, 0, W - crop.Width), Math.Clamp(ip.Y - anchor.Y, 0, H - crop.Height), crop.Width, crop.Height);
        else crop = Span(anchor, ip);
        Changed(estimate: false);
    }

    void StageUp(object sender, MouseButtonEventArgs e)
    {
        if (drag == Drag.None) return;
        drag = Drag.None;
        Stage.ReleaseMouseCapture();
        if (crop.Width < 2 || crop.Height < 2) crop = new Rect(0, 0, W, H); // a click without a drag: back to the whole picture
        Changed();
    }

    /// The frame from a fixed corner to the mouse, held to the ratio and inside the picture.
    Rect Span(Point a, Point p)
    {
        double dx = p.X - a.X, dy = p.Y - a.Y, w = Math.Abs(dx), h = Math.Abs(dy);
        if (ratio is { } r)
        {
            if (w / Math.Max(h, 1e-9) > r) h = w / r; else w = h * r; // follow whichever way the mouse went further
            double roomX = dx >= 0 ? W - a.X : a.X, roomY = dy >= 0 ? H - a.Y : a.Y;
            var f = Math.Min(1, Math.Min(roomX / Math.Max(w, 1e-9), roomY / Math.Max(h, 1e-9)));
            (w, h) = (w * f, h * f);
        }
        return new Rect(dx >= 0 ? a.X : a.X - w, dy >= 0 ? a.Y : a.Y - h, w, h);
    }

    void StageSized(object sender, SizeChangedEventArgs e) => DrawFrame();

    void DrawFrame()
    {
        var visible = src is not null ? Visibility.Visible : Visibility.Collapsed;
        Shade.Visibility = Frame.Visibility = visible;
        foreach (var h in handles) h.Visibility = visible;
        if (src is null || Stage.ActualWidth == 0) return;
        var picture = ToStage(new Rect(0, 0, W, H));
        var frame = ToStage(crop);
        Shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(picture), new RectangleGeometry(frame));
        Canvas.SetLeft(Frame, frame.X);
        Canvas.SetTop(Frame, frame.Y);
        (Frame.Width, Frame.Height) = (frame.Width, frame.Height);
        var corners = Corners(frame);
        for (var i = 0; i < 4; i++)
        {
            Canvas.SetLeft(handles[i], corners[i].X - 4.5);
            Canvas.SetTop(handles[i], corners[i].Y - 4.5);
        }
    }

    // ---- Size and output ----

    void SizeKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SizeCommit(sender, e);
        Keyboard.ClearFocus();
    }

    void SizeCommit(object sender, RoutedEventArgs e)
    {
        if (src is null) return;
        var box = (TextBox)sender;
        if (int.TryParse(box.Text.Trim(), out var v) && v is > 0 and <= 30000)
        {
            if (box == WidthBox) scaleX = v / crop.Width; else scaleY = v / crop.Height;
            if (LockBox.IsChecked == true) { if (box == WidthBox) scaleY = scaleX; else scaleX = scaleY; }
        }
        Changed(); // bad input snaps back
    }

    void QualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (estimateSoon is null) return; // fires during InitializeComponent, before the rest of the page exists
        QualityText.Text = $"{Quality.Value:0}";
        Changed();
    }

    void Changed(bool estimate = true)
    {
        Show();
        if (estimate && src is not null) { estimateSoon.Stop(); estimateSoon.Start(); }
    }

    void Show()
    {
        Controls.IsEnabled = src is not null;
        DropHint.Visibility = src is null ? Visibility.Visible : Visibility.Collapsed;
        QualityRow.Visibility = format.HasQuality ? Visibility.Visible : Visibility.Collapsed;
        QualityText.Text = $"{Quality.Value:0}";
        DrawFrame();
        if (src is null) return;
        var r = Pixels();
        CropText.Text = r.Width == W && r.Height == H ? "Whole picture · drag on it to crop" : $"{r.Width} × {r.Height} from ({r.X}, {r.Y})";
        if (!WidthBox.IsKeyboardFocused) WidthBox.Text = OutW.ToString();
        if (!HeightBox.IsKeyboardFocused) HeightBox.Text = OutH.ToString();
    }

    Int32Rect Pixels()
    {
        int x = (int)Math.Round(crop.X), y = (int)Math.Round(crop.Y);
        return new Int32Rect(x, y, Math.Clamp((int)Math.Round(crop.Width), 1, W - x), Math.Clamp((int)Math.Round(crop.Height), 1, H - y));
    }

    BitmapSource Result() => ImageOps.Resize(ImageOps.Crop(src!, Pixels()), OutW, OutH);

    // Encodes in memory to say how big the file will be. Rendering stays on this thread (WPF), encoding goes to the pool.
    async void Estimate()
    {
        estimateSoon.Stop();
        if (src is null) return;
        var id = ++estimateId;
        var (img, f, q) = (Result(), format, (int)Quality.Value);
        SizeText.Text = "Working out the size…";
        var bytes = await Task.Run(() => ImageOps.Encode(img, f, q).Length);
        if (id == estimateId) SizeText.Text = $"≈ {Units.Bytes(bytes)} · {OutW} × {OutH} px {f.Name}";
    }

    async void Save(object sender, RoutedEventArgs e)
    {
        if (src is null || path is null) return;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var sameType = System.IO.Path.GetExtension(path).Equals(format.Ext, StringComparison.OrdinalIgnoreCase);
        var d = new SaveFileDialog
        {
            Filter = $"{format.Name}|*{format.Ext}", DefaultExt = format.Ext, InitialDirectory = System.IO.Path.GetDirectoryName(path),
            FileName = (sameType ? name + " edited" : name) + format.Ext, // never offer to overwrite the original
        };
        if (d.ShowDialog() != true) return;
        var (img, f, q) = (Result(), format, (int)Quality.Value);
        Status.Text = "Saving…";
        try
        {
            var bytes = await Task.Run(() => ImageOps.Encode(img, f, q));
            await File.WriteAllBytesAsync(d.FileName, bytes);
            Status.Text = $"Saved {System.IO.Path.GetFileName(d.FileName)} · {Units.Bytes(bytes.Length)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = "Couldn't save: " + ex.Message;
        }
    }
}
