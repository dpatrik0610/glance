namespace Glance.Services;

/// Which pages are on (Settings → Pages, saved as Settings.OffPages). The one place to ask: the sidebar, the merged pages'
/// chips, Ctrl+K, the top bar, the tray and every job a page runs outside its own screen (alerts, reminders, hotkeys,
/// desktop windows, polling) check here, so a page that's off goes quiet everywhere. Settings changes raise
/// App.SettingsChanged; anything that must react right away (close windows, re-register a hotkey) listens to that.
public static class Features
{
    /// A page, or a part of a merged page ("Stickies", "Exchange"): on unless it, or the page it's part of, is turned off.
    public static bool On(string name)
    {
        var off = App.Settings.OffPages;
        if (off.Contains(name)) return false;
        var owner = MainWindow.Tabs.FirstOrDefault(t => t.Parts?.Any(p => p.Name == name) == true);
        return owner is null || !off.Contains(owner.Name);
    }

    public static void Set(string name, bool on)
    {
        App.Settings.OffPages.Remove(name);
        if (!on) App.Settings.OffPages.Add(name);
        App.ApplySettings();
    }
}
