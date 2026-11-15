using System.IO;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Glance.Services;

/// Convert, trim, resize and pull audio out of media files for the Media page, on Windows' own transcoder (Media Foundation).
public static class MediaOps
{
    public enum Quality { High, Medium, Low }

    /// Video: keeps the picture. Lossless: no quality choice, keeps the source's sample rate.
    public record Format(string Name, string Ext, bool Video, bool Lossless, Func<Quality, MediaEncodingProfile> Profile);

    static AudioEncodingQuality A(Quality q) => q switch { Quality.High => AudioEncodingQuality.High, Quality.Medium => AudioEncodingQuality.Medium, _ => AudioEncodingQuality.Low };

    public static readonly Format[] Formats =
    [
        new("MP4", ".mp4", true, false, _ => MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p)),
        new("HEVC", ".mp4", true, false, _ => MediaEncodingProfile.CreateHevc(VideoEncodingQuality.HD1080p)),
        new("WMV", ".wmv", true, false, _ => MediaEncodingProfile.CreateWmv(VideoEncodingQuality.HD1080p)),
        new("MP3", ".mp3", false, false, q => MediaEncodingProfile.CreateMp3(A(q))),
        new("M4A", ".m4a", false, false, q => MediaEncodingProfile.CreateM4a(A(q))),
        new("WMA", ".wma", false, false, q => MediaEncodingProfile.CreateWma(A(q))),
        new("FLAC", ".flac", false, true, _ => MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High)),
        new("WAV", ".wav", false, true, _ => MediaEncodingProfile.CreateWav(AudioEncodingQuality.High)),
    ];

    public const string OpenFilter = "Video and audio|*.mp4;*.m4v;*.mov;*.mkv;*.avi;*.wmv;*.webm;*.3gp;*.mp3;*.m4a;*.aac;*.wav;*.flac;*.wma;*.ogg;*.opus;*.alac|All files|*.*";

    /// What's in a file. Video is null for audio only; Audio is null for a silent video.
    public record Info(TimeSpan Duration, VideoEncodingProperties? Video, AudioEncodingProperties? Audio);

    public static async Task<Info> Probe(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var p = await MediaEncodingProfile.CreateFromFileAsync(file);
        var duration = (await file.Properties.GetMusicPropertiesAsync()).Duration; // System.Media.Duration: set for video too
        if (p.Video is null && p.Audio is null) throw new NotSupportedException("no audio or video stream");
        return new(duration, p.Video, p.Audio);
    }

    // Bits per pixel per frame: about where H.264 looks good / fine / soft. HEVC needs about half for the same look.
    static double Bpp(Quality q) => q switch { Quality.High => 0.1, Quality.Medium => 0.06, _ => 0.035 };

    /// Output height for a resize choice: never upscale, and even sizes (encoders want them).
    public static (uint W, uint H) Fit(VideoEncodingProperties src, uint? height)
    {
        if (height is not { } h || h >= src.Height) return (src.Width & ~1u, src.Height & ~1u);
        return ((uint)Math.Round(src.Width * (double)h / src.Height) & ~1u, h & ~1u);
    }

    public static MediaEncodingProfile Profile(Format f, Quality q, Info src, uint? height)
    {
        var p = f.Profile(q);
        if (f.Video && src.Video is { } v)
        {
            var (w, h) = Fit(v, height);
            (p.Video.Width, p.Video.Height) = (w, h);
            if (v.FrameRate.Numerator > 0 && v.FrameRate.Denominator > 0)
                (p.Video.FrameRate.Numerator, p.Video.FrameRate.Denominator) = (v.FrameRate.Numerator, v.FrameRate.Denominator);
            p.Video.Bitrate = (uint)(w * h * Fps(p.Video) * Bpp(q) * (f.Name == "HEVC" ? 0.5 : 1));
        }
        if (f.Video && src.Audio is null) p.Audio = null; // a silent video stays silent instead of failing
        if (f.Lossless && src.Audio is { SampleRate: > 0 } a && p.Audio is { } o)
        {
            o.SampleRate = a.SampleRate;
            if (a.ChannelCount > 0) o.ChannelCount = a.ChannelCount;
            if (a.BitsPerSample is 16 or 24) o.BitsPerSample = a.BitsPerSample; // decoders often say 32 (float); keep the preset's then
            if (f.Name == "WAV") o.Bitrate = o.SampleRate * o.ChannelCount * o.BitsPerSample; // PCM must agree with itself or Windows refuses it
        }
        return p;
    }

    static double Fps(VideoEncodingProperties v) => v.FrameRate.Denominator > 0 ? (double)v.FrameRate.Numerator / v.FrameRate.Denominator : 30;

    /// Rough output size; null when it can't be told up front (FLAC compresses by content).
    public static long? Estimate(Format f, MediaEncodingProfile p, TimeSpan length)
    {
        if (f.Name == "FLAC") return null;
        double bits = (p.Video?.Bitrate ?? 0) + (p.Audio?.Bitrate ?? 0);
        return (long)(bits * length.TotalSeconds / 8);
    }

    /// Writes `dest`; on failure or cancel nothing is left behind. Trim: keep from `start` to `end`.
    public static async Task Convert(string source, string dest, MediaEncodingProfile profile, TimeSpan start, TimeSpan end, TimeSpan duration,
        IProgress<double>? progress, CancellationToken cancel)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Pick a different name: that's the file being converted.");
        var input = await StorageFile.GetFileFromPathAsync(source);
        await File.WriteAllBytesAsync(dest, [], cancel); // the transcoder writes into an existing StorageFile
        try
        {
            var output = await StorageFile.GetFileFromPathAsync(dest);
            var t = new MediaTranscoder { TrimStartTime = start, TrimStopTime = end < duration ? end : TimeSpan.Zero }; // stop is where to end; zero = the end
            var prep = await t.PrepareFileTranscodeAsync(input, output, profile);
            if (!prep.CanTranscode)
                throw new IOException(prep.FailureReason switch
                {
                    TranscodeFailureReason.CodecNotFound => "This PC has no encoder or decoder for that. For HEVC, install HEVC Video Extensions from the Microsoft Store.",
                    TranscodeFailureReason.InvalidProfile => "Windows can't make that format from this file.",
                    _ => "Windows can't convert this file.",
                });
            await prep.TranscodeAsync().AsTask(cancel, progress);
        }
        catch
        {
            try { File.Delete(dest); } catch (IOException) { } // half-written or empty
            throw;
        }
    }

    /// "1:05.3", "65.3", "1:02:03" → time; null if it isn't one.
    public static TimeSpan? ParseTime(string s)
    {
        var parts = s.Trim().Split(':');
        if (parts.Length is 0 or > 3) return null;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0) return null;
            total = total * 60 + n;
        }
        return TimeSpan.FromSeconds(total);
    }

    public static string Time(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");
}
