using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace Glance.Services;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
}

public enum NoteKind { Text, Checklist }

public class TodoItem : Bindable
{
    string text = "";
    bool done, remind, reminded;
    DateTime? due;

    public string Text { get => text; set => Set(ref text, value); }
    public bool Done { get => done; set { if (Set(ref done, value)) Raise(nameof(IsOverdue)); } }
    public DateTime? Due { get => due; set { if (Set(ref due, value)) { Raise(nameof(DueText)); Raise(nameof(IsOverdue)); } } }
    public bool Remind { get => remind; set => Set(ref remind, value); }
    /// Set once the reminder has shown, so it fires once (also across restarts). Notifies, so its note gets saved.
    public bool Reminded { get => reminded; set => Set(ref reminded, value); }

    /// What the due box shows and accepts. Unparseable input is ignored (the box snaps back).
    [JsonIgnore]
    public string DueText
    {
        get => due is { } d ? Services.Due.Format(d, DateTime.Now) : "";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) Due = null;
            else if (Services.Due.Parse(value, DateTime.Now) is { } d && d != due)
            {
                Due = d;
                Reminded = false; // a new time re-arms the reminder
            }
            Raise(nameof(DueText));
        }
    }

    [JsonIgnore] public bool IsOverdue => !done && due < DateTime.Now;
}

/// A note: text or a checklist. A sticky is a note that's also out on the desktop (Sticky), with its own tint,
/// see-through amount and place there.
public class Note : Bindable
{
    /// Glance's own font; a note shows it until another is picked.
    public const string DefaultFont = "Segoe UI Variable Text, Segoe UI";

    /// A sticky's tints: dark, so the text stays as readable as everywhere else in Glance. The dot shows the bright version.
    public static readonly (string Name, string Tint, string Dot)[] Colors =
    [
        ("Graphite", "#1E1E22", "#9C9CA3"), ("Orange", "#3A2016", "#FF6A3D"), ("Yellow", "#3A3214", "#F5C53D"),
        ("Green", "#16301F", "#4CC37A"), ("Blue", "#152A40", "#5AA9FF"), ("Purple", "#2A1D40", "#B07CFF"),
    ];

    string title = "", text = "", font = DefaultFont, color = "Yellow";
    double fontSize = 14, opacity = 0.85, width = 280, height = 240;
    double? left, top; // null until placed
    bool sticky;
    DateTime? edited;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonConverter(typeof(JsonStringEnumConverter))] public NoteKind Kind { get; set; }
    public string Title { get => title; set { if (Set(ref title, value)) Raise(nameof(DisplayTitle)); } }
    public string Text { get => text; set { if (Set(ref text, value)) Touch(); } }
    public ObservableCollection<TodoItem> Items { get; set; } = [];
    /// How the body (text or checklist items) is written.
    public string Font { get => font; set => Set(ref font, string.IsNullOrWhiteSpace(value) ? DefaultFont : value); }
    public double FontSize { get => fontSize; set => Set(ref fontSize, Math.Clamp(Math.Round(value), 9, 40)); }
    /// Last change to the title or body (set by JsonStore); null for notes from before it was kept.
    public DateTime? Edited { get => edited; set { if (Set(ref edited, value)) { Raise(nameof(When)); Raise(nameof(Meta)); } } }

    /// On the desktop as a sticky (when stickies are shown at all: Settings.StickiesShown).
    public bool Sticky { get => sticky; set => Set(ref sticky, value); }
    public string Color { get => color; set { if (Set(ref color, value)) Raise(nameof(Dot)); } }
    /// How solid the sticky's tint is over the blurred desktop (0.3..1).
    public double Opacity { get => opacity; set => Set(ref opacity, value); }
    public double? Left { get => left; set => Set(ref left, value); }
    public double? Top { get => top; set => Set(ref top, value); }
    public double Width { get => width; set => Set(ref width, value); }
    public double Height { get => height; set => Set(ref height, value); }

    [JsonIgnore] public string DisplayTitle => title.Trim().Length > 0 ? title : "Untitled";
    [JsonIgnore] public string Icon => Kind == NoteKind.Text ? "\uE70B" : "\uE9D5";
    [JsonIgnore] public (string Name, string Tint, string Dot) Palette => Colors.FirstOrDefault(c => c.Name == color, Colors[2]);
    [JsonIgnore] public string Dot => Palette.Dot;
    [JsonIgnore] public string When => edited is { } e ? Cloudflare.Ago(e) : "";

    /// The start of the text, or the checklist's progress and open items.
    [JsonIgnore]
    public string Summary
    {
        get
        {
            if (Kind == NoteKind.Text)
            {
                var flat = string.Join(' ', text.Split((char[]?)null, 60, StringSplitOptions.RemoveEmptyEntries));
                return flat.Length > 0 ? flat : "Empty";
            }
            var open = Items.Where(i => !i.Done && i.Text.Trim().Length > 0).Select(i => i.Text.Trim()).Take(8).ToList();
            return $"{Items.Count(i => i.Done)}/{Items.Count} done" + (open.Count > 0 ? " · " + string.Join(", ", open) : "");
        }
    }

    /// When the next open checklist item is due ("" when none is).
    [JsonIgnore] public string NextDue => Items.Where(i => !i.Done && i.Due is not null).MinBy(i => i.Due)?.DueText ?? "";

    /// "Edited 5 min ago · 412 words" (or "3/5 done" for a checklist).
    [JsonIgnore]
    public string Meta
    {
        get
        {
            var size = Kind == NoteKind.Text
                ? text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length switch { 1 => "1 word", var n => $"{n} words" }
                : $"{Items.Count(i => i.Done)}/{Items.Count} done";
            return edited is null ? size : $"Edited {When} · {size}";
        }
    }

    /// The body changed (its text, or an item of its checklist).
    public void Touch()
    {
        Raise(nameof(Summary));
        Raise(nameof(NextDue));
        Raise(nameof(Meta));
    }
}

/// A list saved as JSON in %AppData%\Glance. Any edit (to an item, its checklist or the list) saves after a short pause;
/// exit flushes. A file that can't be read is set aside, never overwritten.
public sealed class JsonStore<T> where T : Note
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly string file;
    readonly Debounce pendingSave;

    public ObservableCollection<T> Items { get; private set; } = [];

    public JsonStore(string name)
    {
        file = Path.Combine(Settings.Dir, name);
        pendingSave = new(TimeSpan.FromMilliseconds(800), Flush);
    }

    /// missing: what to start with when there's no file yet (null: empty). Returns whether that happened.
    public bool Load(Func<IEnumerable<T>?>? missing = null)
    {
        var created = !File.Exists(file);
        Items = Settings.ReadJson<ObservableCollection<T>>(file, Json) ?? new(created ? missing?.Invoke() ?? [] : []);

        foreach (var n in Items) Watch(n);
        Items.CollectionChanged += (_, e) =>
        {
            foreach (T n in e.NewItems ?? Array.Empty<T>()) Watch(n);
            SaveSoon();
        };
        return created;
    }

    void Watch(T n)
    {
        n.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Note.Title) or nameof(Note.Summary)) n.Edited = DateTime.Now; // its title or body
            SaveSoon();
        };
        n.Items.CollectionChanged += (_, e) =>
        {
            foreach (TodoItem i in e.NewItems ?? Array.Empty<TodoItem>()) WatchItem(n, i);
            n.Touch();
        };
        foreach (var i in n.Items) WatchItem(n, i);
    }

    static void WatchItem(T n, TodoItem i) => i.PropertyChanged += (_, _) => n.Touch();

    /// Raised on any edit: to a note, its checklist or the list.
    public event Action? Changed;

    public void SaveSoon()
    {
        pendingSave.Poke();
        Changed?.Invoke();
    }

    public void Flush()
    {
        pendingSave.Cancel();
        try { Settings.WriteAtomic(file, JsonSerializer.Serialize(Items, Json)); }
        catch (IOException e)
        {
            Db.Error(Path.GetFileName(file) + " save", e);
            pendingSave.Poke(); // retry; the data is still in memory
        }
    }
}

/// All notes (stickies too), in notes.json.
public static class NoteStore
{
    static readonly JsonStore<Note> store = new("notes.json");
    static readonly string LegacyFile = Path.Combine(Settings.Dir, "notes.txt");
    static readonly string StickiesFile = Path.Combine(Settings.Dir, "stickies.json");

    public static ObservableCollection<Note> Notes => store.Items;

    /// Raised on any edit to the notes.
    public static event Action? Changed { add => store.Changed += value; remove => store.Changed -= value; }

    /// Raised when something (a reminder click) wants a note shown.
    public static event Action<Note>? FocusRequested;
    public static void RequestFocus(Note n) => FocusRequested?.Invoke(n);

    public static void Load()
    {
        // First run of multi-note: import the old single pad.
        var legacy = File.Exists(LegacyFile);
        if (store.Load(() => legacy ? [new Note { Title = "Notes", Text = File.ReadAllText(LegacyFile) }] : null) && legacy)
        {
            store.Flush();
            File.Move(LegacyFile, LegacyFile + ".imported", true);
        }
        // Stickies used to be a list of their own (stickies.json): they join the notes, still on the desktop.
        if (Settings.ReadJson<List<OldSticky>>(StickiesFile) is { } stickies)
        {
            foreach (var s in stickies) Notes.Add(s);
            store.Flush();
            File.Move(StickiesFile, StickiesFile + ".imported", true);
        }
    }

    /// A sticky from stickies.json, where "Shown" was its on-the-desktop switch. Saved as a plain Note (the list's type).
    internal sealed class OldSticky : Note
    {
        public OldSticky() => Sticky = true;
        public bool Shown { get => Sticky; set => Sticky = value; }
    }

    public static void SaveSoon() => store.SaveSoon();

    /// Today's daily note (a text note titled yyyy-MM-dd), made if there isn't one.
    public static Note Today()
    {
        var name = DateTime.Today.ToString("yyyy-MM-dd");
        if (Notes.FirstOrDefault(n => n.Kind == NoteKind.Text && n.Title.Trim() == name) is { } n) return n;
        n = new Note { Title = name };
        Notes.Insert(0, n);
        return n;
    }

    /// Items whose reminder is due now and hasn't shown yet.
    public static IEnumerable<(Note Note, TodoItem Item)> DueReminders(DateTime now) =>
        from n in Notes
        from i in n.Items
        where i.Remind && !i.Done && !i.Reminded && i.Due <= now
        select (n, i);
}
