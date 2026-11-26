using System.ComponentModel;

namespace Glance.Services;

/// The desktop windows of the notes that are stickies. A sticky is on the desktop when its note is Sticky and stickies
/// are shown at all (Settings.StickiesShown, the tray switch).
public static class Stickies
{
    static readonly Dictionary<Note, StickyWindow> open = [];

    public static void Load()
    {
        foreach (var n in NoteStore.Notes) n.PropertyChanged += StickyChanged;
        NoteStore.Notes.CollectionChanged += (_, e) =>
        {
            foreach (Note n in e.NewItems ?? Array.Empty<Note>()) n.PropertyChanged += StickyChanged;
            Sync();
        };
        App.SettingsChanged += Sync;
        Sync();
    }

    static void StickyChanged(object? o, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Note.Sticky)) Sync();
    }

    /// Opens the windows that should be out and closes the rest (also those of deleted notes).
    static void Sync()
    {
        foreach (var n in NoteStore.Notes)
        {
            var want = App.Settings.StickiesShown && Features.On("Notes") && n.Sticky;
            if (want && !open.ContainsKey(n))
            {
                var w = new StickyWindow(n);
                open[n] = w;
                w.Closed += (_, _) => open.Remove(n);
                w.Show();
            }
            else if (!want && open.Remove(n, out var gone)) gone.Close();
        }
        foreach (var deleted in open.Keys.Except(NoteStore.Notes).ToList())
            if (open.Remove(deleted, out var w)) w.Close();
    }

    /// Adds the note at the top of the list and puts it on the desktop.
    public static Note Add(Note n)
    {
        n.Sticky = true;
        NoteStore.Notes.Insert(0, n);
        Reveal(n);
        return n;
    }

    /// Puts it on the desktop (turning stickies on if they were hidden) and brings it to the front.
    public static void Reveal(Note n)
    {
        n.Sticky = true;
        if (!App.Settings.StickiesShown) SetShown(true);
        if (open.TryGetValue(n, out var w)) w.Activate();
    }

    public static void SetShown(bool on)
    {
        App.Settings.StickiesShown = on;
        App.ApplySettings(); // Sync runs on SettingsChanged
    }
}
