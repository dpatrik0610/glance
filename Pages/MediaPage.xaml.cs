using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Glance.Services;
using Microsoft.Win32;

namespace Glance.Pages;

/// Convert, trim and resize one video or audio file, or pull the sound out of a video. Nothing is written until Save.
public partial class MediaPage : UserControl
{
    string? path;
    MediaOps.Info? info;
    TimeSpan start, end;
    uint? height;                   // resize to this many lines; null = as it is
    MediaOps.Format format = MediaOps.Formats[0];
    MediaOps.Quality quality = MediaOps.Quality.High;
    CancellationTokenSource? working;
    bool playing, ticking;
    readonly DispatcherTimer tick;

    TimeSpan Duration => info!.Duration;

    public MediaPage()
    {
        InitializeComponent();
        foreach (var f in MediaOps.Formats)
        {
            var chip = Chip(f.Name, "mediaformat", f == format);
            chip.Tag = f;
            chip.Checked += (_, _) => { format = f; Show(); };
            FormatChips.Children.Add(chip);
        }
        foreach (var q in Enum.GetValues<MediaOps.Quality>())
        {
            var chip = Chip(q.ToString(), "mediaquality", q == quality);
            chip.Checked += (_, _) => { quality = q; Show(); };
            Qualities.Children.Add(chip);
        }
        tick = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        IsVisibleChanged += (_, _) => { if (!IsVisible) Play(false); };
        Show();
    }

    RadioButton Chip(string text, string group, bool on) =>
        new() { Content = text, GroupName = group, IsChecked = on, Style = (Style)FindResource("Chip"), Margin = new(0, 0, 6, 6) };

    // ---- Opening ----

    void OpenClick(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = MediaOps.OpenFilter };
        if (d.ShowDialog() == true) Open(d.FileName);
    }

    void DraggedOver(object sender, DragEventArgs e)
    {
        e.Effects = working is null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Dropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) Open(files[0]);
    }

    async void Open(string file)
    {
        if (working is not null) return;
        Status.Text = "Opening…";
        MediaOps.Info found;
        try { found = await MediaOps.Probe(file); }
        catch (Exception) // WinRT reports an unreadable file as several different exception types
        {
            Status.Text = $"Can't open {Path.GetFileName(file)}: not a video or audio file Windows can read.";
            return;
        }
        Play(false);
        (path, info, start, end, height) = (file, found, TimeSpan.Zero, found.Duration, null);

        // Sizes below the source's; video formats only when there's a picture.
        Sizes.Children.Clear();
        var heights = new uint?[] { null, 2160, 1440, 1080, 720, 480, 360 }.Where(h => h is null || found.Video is { } v && h < v.Height);
        foreach (var h in heights)
        {
            var chip = Chip(h is null ? "Original" : h + "p", "mediasize", h is null);
            chip.Checked += (_, _) => { height = h; Show(); };
            Sizes.Children.Add(chip);
        }
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var pick = MediaOps.Formats.FirstOrDefault(f => f.Ext == ext && (f.Video || found.Video is null) && f.Name != "HEVC")
                   ?? MediaOps.Formats.First(f => f.Video == (found.Video is not null));
        foreach (RadioButton chip in FormatChips.Children)
        {
            var f = (MediaOps.Format)chip.Tag;
            chip.Visibility = f.Video && found.Video is null ? Visibility.Collapsed : Visibility.Visible;
            if (f == pick) chip.IsChecked = true;
        }

        var v = found.Video;
        FileText.Text = $"{Path.GetFileName(file)} · {MediaOps.Time(found.Duration)}" + (v is null ? "" : $" · {v.Width} × {v.Height}")
                        + $" · {ImageOps.Size(new FileInfo(file).Length)}";
        DropHint.Text = v is null ? "" : "";
        Status.Text = "";
        Player.IsEnabled = false;
        Preview.Source = new Uri(file);
        Preview.Pause(); // loads it and shows the first frame
        Show();
    }

    // ---- Preview player ----

    void PreviewOpened(object sender, RoutedEventArgs e)
    {
        if (info is null) return;
        if (Duration == TimeSpan.Zero && Preview.NaturalDuration.HasTimeSpan) // the file didn't say; the player knows
            info = info with { Duration = Preview.NaturalDuration.TimeSpan };
        if (end == TimeSpan.Zero) end = Duration;
        Seek.Maximum = Duration.TotalSeconds;
        DropHint.Visibility = info.Video is null ? Visibility.Visible : Visibility.Collapsed;
        Player.IsEnabled = true;
        Show();
        Tick();
    }

    void PreviewFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Player.IsEnabled = false;
        Status.Text = "No preview for this file, but it can still be converted.";
    }

    void PreviewEnded(object sender, RoutedEventArgs e) => Play(false);

    void PlayClick(object sender, RoutedEventArgs e) => Play(!playing);

    void Play(bool on)
    {
        playing = on && info is not null;
        if (playing) { Preview.Play(); tick.Start(); }
        else { Preview.Pause(); tick.Stop(); }
        PlayButton.Content = playing ? "" : "";
        Tick();
    }

    void Tick()
    {
        if (info is null) return;
        ticking = true;
        Seek.Value = Preview.Position.TotalSeconds;
        ticking = false;
        TimeText.Text = $"{MediaOps.Time(Preview.Position)} / {MediaOps.Time(Duration)}";
    }

    void Seeked(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ticking || info is null) return;
        Preview.Position = TimeSpan.FromSeconds(e.NewValue);
        TimeText.Text = $"{MediaOps.Time(Preview.Position)} / {MediaOps.Time(Duration)}";
    }

    // ---- Trim ----

    static readonly TimeSpan Shortest = TimeSpan.FromSeconds(0.1);

    void StartHere(object sender, RoutedEventArgs e) => SetTrim(Preview.Position, end);
    void EndHere(object sender, RoutedEventArgs e) => SetTrim(start, Preview.Position);
    void ResetTrim(object sender, RoutedEventArgs e) => SetTrim(TimeSpan.Zero, Duration);

    void TimeKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        TimeCommit(sender, e);
        Keyboard.ClearFocus();
    }

    void TimeCommit(object sender, RoutedEventArgs e)
    {
        if (info is null) return;
        var box = (TextBox)sender;
        if (MediaOps.ParseTime(box.Text) is { } t)
        {
            if (box == StartBox) SetTrim(t, end); else SetTrim(start, t);
        }
        else Show(); // bad input snaps back
    }

    /// Takes the new range only if it makes sense: inside the file, start before end.
    void SetTrim(TimeSpan s, TimeSpan e)
    {
        if (info is null) return;
        e = e > Duration ? Duration : e;
        if (s >= TimeSpan.Zero && e - s >= Shortest) (start, end) = (s, e);
        Show();
    }

    // ---- Output ----

    void Show()
    {
        Controls.IsEnabled = info is not null;
        DropHint.Visibility = info is null || info.Video is null ? Visibility.Visible : Visibility.Collapsed;
        QualityRow.Visibility = format.Lossless ? Visibility.Collapsed : Visibility.Visible;
        if (info is null) return;

        if (!StartBox.IsKeyboardFocused) StartBox.Text = MediaOps.Time(start);
        if (!EndBox.IsKeyboardFocused) EndBox.Text = MediaOps.Time(end);
        var length = end - start;
        TrimText.Text = start == TimeSpan.Zero && end == Duration ? $"Whole file · {MediaOps.Time(Duration)}" : $"Keeps {MediaOps.Time(length)} of {MediaOps.Time(Duration)}";

        var video = format.Video ? info.Video : null;
        ResizePanel.Visibility = video is null ? Visibility.Collapsed : Visibility.Visible;
        if (video is not null)
        {
            var (w, h) = MediaOps.Fit(video, height);
            ResizeText.Text = $"{w} × {h}";
        }
        FormatText.Text = info.Video is not null && !format.Video ? "Just the sound, no picture."
            : format.Name == "HEVC" ? "About half the size of MP4 for the same look. Needs HEVC Video Extensions from the Microsoft Store."
            : format.Lossless ? "Lossless: exactly the sound in the file, bigger." : "";
        FormatText.Visibility = FormatText.Text == "" ? Visibility.Collapsed : Visibility.Visible;

        var size = MediaOps.Estimate(format, MediaOps.Profile(format, quality, info, height), length);
        SizeText.Text = (size is { } b ? $"≈ {ImageOps.Size(b)} · " : "") + $"{MediaOps.Time(length)} {format.Name}";
    }

    async void Save(object sender, RoutedEventArgs e)
    {
        if (info is null || path is null || working is not null) return;
        var name = Path.GetFileNameWithoutExtension(path);
        var sameType = Path.GetExtension(path).Equals(format.Ext, StringComparison.OrdinalIgnoreCase);
        var d = new SaveFileDialog
        {
            Filter = $"{format.Name}|*{format.Ext}", DefaultExt = format.Ext, InitialDirectory = Path.GetDirectoryName(path),
            FileName = (sameType ? name + " edited" : name) + format.Ext, // never offer to overwrite the original
        };
        if (d.ShowDialog() != true) return;

        Play(false);
        working = new CancellationTokenSource();
        foreach (UIElement c in Controls.Children) c.IsEnabled = c == Working || c == Status;
        SaveButton.Visibility = Visibility.Collapsed;
        Working.Visibility = Visibility.Visible;
        Bar.Value = 0;
        Status.Text = "Converting…";
        try
        {
            await MediaOps.Convert(path, d.FileName, MediaOps.Profile(format, quality, info, height), start, end, Duration,
                new Progress<double>(p => Bar.Value = p), working.Token);
            Status.Text = $"Saved {Path.GetFileName(d.FileName)} · {ImageOps.Size(new FileInfo(d.FileName).Length)}";
        }
        catch (OperationCanceledException) { Status.Text = "Cancelled."; }
        catch (Exception ex) { Status.Text = "Couldn't convert: " + ex.Message; } // IO, access, and the transcoder's COM errors
        finally
        {
            working.Dispose();
            working = null;
            foreach (UIElement c in Controls.Children) c.IsEnabled = true;
            SaveButton.Visibility = Visibility.Visible;
            Working.Visibility = Visibility.Collapsed;
        }
    }

    void CancelClick(object sender, RoutedEventArgs e) => working?.Cancel();
}
