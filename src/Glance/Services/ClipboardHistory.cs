using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Glance.Services;

/// Text, or an image (Png set: the picture as PNG, Thumb: a small decoded copy for the list).
public record ClipItem(string Text, DateTime Time, byte[]? Png = null, BitmapSource? Thumb = null)
{
    public string Preview => Text.Length > 400 ? Text[..400] : Text;
    public string When => Time.ToString("HH:mm");
    public bool IsImage => Png is not null;
    /// What counts as the same entry: the text, or the image's bytes.
    public string Key { get; init; } = Text;
}

/// Clipboard history, in memory only (never written to disk or Mongo: it can hold secrets). An image is written to a
/// temp file only when you open it, and those are deleted on exit.
/// Honors the formats password managers set to opt out of clipboard monitors.
public static class ClipboardHistory
{
    const int Limit = 100, ImageLimit = 20; // images are big: only the newest 20 stay
    public static ObservableCollection<ClipItem> Items { get; } = [];

    // Called on WM_CLIPBOARDUPDATE (UI thread).
    public static void Capture()
    {
        if (!App.Settings.ClipboardHistory) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || IsPrivate(data)) return;
            if (data.GetDataPresent(DataFormats.UnicodeText)) // text wins when both are there (cells copied from a sheet)
            {
                if (data.GetData(DataFormats.UnicodeText) is not string text || string.IsNullOrWhiteSpace(text) || text.Length > 1_000_000) return;
                Add(new(text, DateTime.Now));
            }
            else if (App.Settings.ClipboardImages)
            {
                // "PNG" (browsers, Office, Snipping Tool) keeps transparency. Otherwise the bitmap, whose alpha channel is often
                // garbage (screenshots read as fully transparent), so it's taken as opaque.
                if (data.GetData("PNG") is MemoryStream png) AddImage(png.ToArray(), null);
                else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bmp) AddImage(null, Opaque(bmp));
            }
        }
        catch (COMException) { } // another app is holding the clipboard; skip this change
    }

    static void Add(ClipItem item)
    {
        if (Items.FirstOrDefault(i => i.Key == item.Key) is { } dup) Items.Remove(dup);
        Items.Insert(0, item);
        while (Items.Count > Limit) Items.RemoveAt(Items.Count - 1);
        foreach (var old in Items.Where(i => i.IsImage).Skip(ImageLimit).ToList()) Items.Remove(old);
    }

    // Encoding a big screenshot takes a moment: done off the UI thread, then added.
    static async void AddImage(byte[]? png, BitmapSource? pixels)
    {
        var time = DateTime.Now;
        try
        {
            var (bytes, thumb, size) = await Task.Run(() =>
            {
                var b = png ?? Encode(pixels!);
                var frame = BitmapDecoder.Create(new MemoryStream(b), BitmapCreateOptions.None, BitmapCacheOption.None).Frames[0];
                return (b, Decode(b, 200), $"{frame.PixelWidth} × {frame.PixelHeight}");
            });
            Add(new ClipItem("Image " + size, time, bytes, thumb) { Key = "image " + Convert.ToHexString(SHA256.HashData(bytes)) });
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or COMException)
        {
            Db.Warn("clipboard image: " + e.Message); // a format WIC can't read
        }
    }

    static BitmapSource Opaque(BitmapSource s)
    {
        var c = new FormatConvertedBitmap(s, PixelFormats.Bgr32, null, 0);
        var stride = c.PixelWidth * 4;
        var px = new byte[stride * c.PixelHeight];
        c.CopyPixels(px, stride, 0);
        var b = BitmapSource.Create(c.PixelWidth, c.PixelHeight, 96, 96, PixelFormats.Bgr32, null, px, stride);
        b.Freeze(); // so the encoder thread can read it
        return b;
    }

    public static byte[] Encode(BitmapSource b)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// height 0: full size.
    static BitmapImage Decode(byte[] png, int height)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(png);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelHeight = height;
        img.EndInit();
        img.Freeze();
        return img;
    }

    public static bool CopyImage(ClipItem item)
    {
        var d = new DataObject();
        d.SetImage(Decode(item.Png!, 0));
        d.SetData("PNG", new MemoryStream(item.Png!)); // apps that read it keep transparency
        try { Clipboard.SetDataObject(d, true); return true; }
        catch (COMException) { return false; }
    }

    static readonly string TempDir = Path.Combine(Path.GetTempPath(), "Glance", "clipboard");
    static bool cleanupHooked;

    /// In the default image viewer, through a temp file deleted when Glance exits.
    public static void Open(ClipItem item)
    {
        Directory.CreateDirectory(TempDir);
        var path = Path.Combine(TempDir, $"clipboard {item.Time:yyyy-MM-dd HH.mm.ss}.png");
        File.WriteAllBytes(path, item.Png!);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        if (cleanupHooked) return;
        cleanupHooked = true;
        Application.Current.Exit += (_, _) =>
        {
            try { Directory.Delete(TempDir, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // still open in the viewer
        };
    }

    static bool IsPrivate(IDataObject data) =>
        data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")
        || data.GetDataPresent("Clipboard Viewer Ignore")
        || data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4 && BitConverter.ToInt32(ms.ToArray(), 0) == 0;

    public static bool Copy(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (COMException) { return false; }
    }

    /// Copies without landing in any clipboard history (ours, Win+V, cloud), and wipes it after a while if unchanged.
    public static bool CopySecret(string text, TimeSpan clearAfter)
    {
        var d = new DataObject();
        d.SetText(text);
        d.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        d.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        d.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        try { Clipboard.SetDataObject(d, true); }
        catch (COMException) { return false; }

        var timer = new DispatcherTimer { Interval = clearAfter };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear(); }
            catch (COMException) { }
        };
        timer.Start();
        return true;
    }
}
