using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Glance.Services;

public record ClipItem(string Text, DateTime Time)
{
    public string Preview => Text.Length > 400 ? Text[..400] : Text;
    public string When => Time.ToString("HH:mm");
}

/// Text-only clipboard history, in memory only (never written to disk or Mongo: it can hold secrets).
/// Honors the formats password managers set to opt out of clipboard monitors.
public static class ClipboardHistory
{
    const int Limit = 100;
    public static ObservableCollection<ClipItem> Items { get; } = [];

    // Called on WM_CLIPBOARDUPDATE (UI thread).
    public static void Capture()
    {
        if (!App.Settings.ClipboardHistory) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || !data.GetDataPresent(DataFormats.UnicodeText) || IsPrivate(data)) return;
            if (data.GetData(DataFormats.UnicodeText) is not string text || string.IsNullOrWhiteSpace(text) || text.Length > 1_000_000) return;
            if (Items.FirstOrDefault(i => i.Text == text) is { } dup) Items.Remove(dup);
            Items.Insert(0, new(text, DateTime.Now));
            while (Items.Count > Limit) Items.RemoveAt(Items.Count - 1);
        }
        catch (COMException) { } // another app is holding the clipboard; skip this change
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
