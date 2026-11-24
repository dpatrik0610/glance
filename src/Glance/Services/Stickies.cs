using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Glance.Services;

/// The desktop stickies: the list (stickies.json) and a window for each one that's out.
/// A sticky is on the desktop when it's Shown and stickies are shown at all (Settings.StickiesShown, the tray switch).
public static class Stickies
{
    static readonly JsonStore<Sticky> store = new("stickies.json");
    static readonly Dictionary<Sticky, StickyWindow> open = [];

    public static ObservableCollection<Sticky> Items => store.Items;

    public static void Load()
    {
        store.Load();
        foreach (var s in Items) s.PropertyChanged += ShownChanged;
        Items.CollectionChanged += (_, e) =>
        {
            foreach (Sticky s in e.NewItems ?? Array.Empty<Sticky>()) s.PropertyChanged += ShownChanged;
            Sync();
        };
        App.SettingsChanged += Sync;
        Sync();
    }

    static void ShownChanged(object? o, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Sticky.Shown)) Sync();
    }

    /// Opens the windows that should be out and closes the rest (also those of deleted stickies).
    static void Sync()
    {
        foreach (var s in Items)
        {
            var want = App.Settings.StickiesShown && Features.On("Stickies") && s.Shown;
            if (want && !open.ContainsKey(s))
            {
                var w = new StickyWindow(s);
                open[s] = w;
                w.Closed += (_, _) => open.Remove(s);
                w.Show();
            }
            else if (!want && open.Remove(s, out var gone)) gone.Close();
        }
        foreach (var deleted in open.Keys.Except(Items).ToList())
            if (open.Remove(deleted, out var w)) w.Close();
    }

    public static Sticky Add(NoteKind kind)
    {
        var s = new Sticky { Kind = kind };
        Items.Insert(0, s);
        Reveal(s);
        return s;
    }

    /// Puts it on the desktop (turning stickies on if they were hidden) and brings it to the front.
    public static void Reveal(Sticky s)
    {
        s.Shown = true;
        if (!App.Settings.StickiesShown) SetShown(true);
        if (open.TryGetValue(s, out var w)) w.Activate();
    }

    public static void SetShown(bool on)
    {
        App.Settings.StickiesShown = on;
        App.ApplySettings(); // Sync runs on SettingsChanged
    }
}
