using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glance.Services;

/// Load, crop, resize and encode for the Images page, all on WPF's built-in codecs (WIC).
public static class ImageOps
{
    public record Format(string Name, string Ext, bool HasQuality);

    public static readonly Format[] Formats =
    [
        new("JPG", ".jpg", true), new("PNG", ".png", false), new("BMP", ".bmp", false), new("GIF", ".gif", false), new("TIFF", ".tif", false),
    ];

    /// What the Open dialog offers: everything WIC reads here, incl. WebP/HEIC when Windows has those codecs.
    public const string OpenFilter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.webp;*.heic;*.heif;*.jxr;*.wdp|All files|*.*";

    public static BitmapSource Load(string path)
    {
        using var f = File.OpenRead(path);
        return Load(f);
    }

    /// First frame, turned upright the way the camera's EXIF orientation says (phones store photos sideways).
    public static BitmapSource Load(Stream s)
    {
        var frame = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        ushort orientation = 1;
        try { if (frame.Metadata is BitmapMetadata m && m.GetQuery("System.Photo.Orientation") is ushort o) orientation = o; }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { } // format without metadata
        BitmapSource b = frame;
        // EXIF: mirror first, then turn clockwise.
        var (mirrorX, mirrorY, angle) = orientation switch
        {
            2 => (true, false, 0), 3 => (false, false, 180), 4 => (false, true, 0), 5 => (true, false, 270),
            6 => (false, false, 90), 7 => (true, false, 90), 8 => (false, false, 270), _ => (false, false, 0),
        };
        if (mirrorX || mirrorY || angle != 0)
        {
            var t = new TransformGroup();
            if (mirrorX || mirrorY) t.Children.Add(new ScaleTransform(mirrorX ? -1 : 1, mirrorY ? -1 : 1));
            if (angle != 0) t.Children.Add(new RotateTransform(angle));
            b = new TransformedBitmap(frame, t);
        }
        // Copied out of the decoder: a frozen frame still asks its decoder (owned by the loading thread) things later, so
        // cropping it on the UI thread after a background load would throw.
        var stride = (b.PixelWidth * b.Format.BitsPerPixel + 7) / 8;
        var px = new byte[(long)stride * b.PixelHeight];
        b.CopyPixels(px, stride, 0);
        var copy = BitmapSource.Create(b.PixelWidth, b.PixelHeight, b.DpiX, b.DpiY, b.Format, b.Palette, px, stride);
        copy.Freeze();
        return copy;
    }

    public static BitmapSource Crop(BitmapSource src, Int32Rect r)
    {
        if (r.X == 0 && r.Y == 0 && r.Width == src.PixelWidth && r.Height == src.PixelHeight) return src;
        var c = new CroppedBitmap(src, r);
        c.Freeze();
        return c;
    }

    /// High-quality (Fant) scaling, drawn at 96 dpi so the pixel size is exactly w × h.
    public static BitmapSource Resize(BitmapSource src, int w, int h, Color? background = null)
    {
        if (w == src.PixelWidth && h == src.PixelHeight && background is null) return src;
        var v = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(v, BitmapScalingMode.HighQuality);
        using (var dc = v.RenderOpen())
        {
            if (background is { } bg) dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, w, h));
            dc.DrawImage(src, new Rect(0, 0, w, h));
        }
        var rt = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rt.Render(v);
        rt.Freeze();
        return rt;
    }

    public static bool HasAlpha(BitmapSource b) => b.Format == PixelFormats.Bgra32 || b.Format == PixelFormats.Pbgra32
        || b.Format == PixelFormats.Rgba64 || b.Format == PixelFormats.Prgba64 || b.Format == PixelFormats.Rgba128Float
        || b.Format == PixelFormats.Prgba128Float;

    /// quality: 1..100, JPG only. JPG and BMP can't hold transparency: it's flattened onto white (not black).
    public static byte[] Encode(BitmapSource b, Format f, int quality)
    {
        if (f.Name is "JPG" or "BMP" && HasAlpha(b)) b = Resize(b, b.PixelWidth, b.PixelHeight, Colors.White);
        BitmapEncoder enc = f.Name switch
        {
            "JPG" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
            "PNG" => new PngBitmapEncoder(),
            "BMP" => new BmpBitmapEncoder(),
            "GIF" => new GifBitmapEncoder(),
            _ => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
        };
        enc.Frames.Add(BitmapFrame.Create(b));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
