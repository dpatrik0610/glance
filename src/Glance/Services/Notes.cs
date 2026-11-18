using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;

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

public class Note : Bindable
{
    /// Glance's own font; a note shows it until another is picked.
    public const string DefaultFont = "Segoe UI Variable Text, Segoe UI";

    string title = "", text = "", font = DefaultFont;
    double fontSize = 14;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonConverter(typeof(JsonStringEnumConverter))] public NoteKind Kind { get; set; }
    public string Title { get => title; set { if (Set(ref title, value)) Raise(nameof(DisplayTitle)); } }
    public string Text { get => text; set { if (Set(ref text, value)) Raise(nameof(Summary)); } }
    public ObservableCollection<TodoItem> Items { get; set; } = [];
    /// How the body (text or checklist items) is written.
    public string Font { get => font; set => Set(ref font, string.IsNullOrWhiteSpace(value) ? DefaultFont : value); }
    public double FontSize { get => fontSize; set => Set(ref fontSize, Math.Clamp(Math.Round(value), 9, 40)); }

    [JsonIgnore] public string DisplayTitle => title.Trim().Length > 0 ? title : "Untitled";
    [JsonIgnore] public string Icon => Kind == NoteKind.Text ? "\uE70B" : "\uE9D5";

    [JsonIgnore]
    public string Summary
    {
        get
        {
            if (Kind == NoteKind.Text)
            {
                var line = text.TrimStart().Split('\n', 2)[0].Trim();
                return line.Length > 0 ? line : "Empty";
            }
            var next = Items.Where(i => !i.Done && i.Due is not null).MinBy(i => i.Due);
            return $"{Items.Count(i => i.Done)}/{Items.Count} done" + (next is null ? "" : " · " + next.DueText);
        }
    }

    public void Touch() => Raise(nameof(Summary));
}


/// A note that lives on the desktop: always on top, with its own tint, see-through amount and place.
public class Sticky : Note
{
    /// Dark tints, so the text stays as readable as everywhere else in Glance. The dot shows the bright version.
    public static readonly (string Name, string Tint, string Dot)[] Colors =
    [
        ("Graphite", "#1E1E22", "#9C9CA3"), ("Orange", "#3A2016", "#FF6A3D"), ("Yellow", "#3A3214", "#F5C53D"),
        ("Green", "#16301F", "#4CC37A"), ("Blue", "#152A40", "#5AA9FF"), ("Purple", "#2A1D40", "#B07CFF"),
    ];

    string color = "Yellow";
    double opacity = 0.85, width = 280, height = 240;
    double? left, top; // null until placed
    bool shown = true;

    public string Color { get => color; set { if (Set(ref color, value)) Raise(nameof(Dot)); } }
    /// How solid the tint is over the blurred desktop (0.3..1).
    public double Opacity { get => opacity; set => Set(ref opacity, value); }
    public double? Left { get => left; set => Set(ref left, value); }
    public double? Top { get => top; set => Set(ref top, value); }
    public double Width { get => width; set => Set(ref width, value); }
    public double Height { get => height; set => Set(ref height, value); }
    /// On the desktop (when stickies are shown at all: Settings.StickiesShown).
    public bool Shown { get => shown; set => Set(ref shown, value); }

    [JsonIgnore] public (string Name, string Tint, string Dot) Palette => Colors.FirstOrDefault(c => c.Name == color, Colors[2]);
    [JsonIgnore] public string Dot => Palette.Dot;
}

/// A list saved as JSON in %AppData%\Glance. Any edit (to an item, its checklist or the list) saves after a short pause;
/// exit flushes. A file that can't be read is set aside, never overwritten.
public sealed class JsonStore<T> where T : Note
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly string file;
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };

    public ObservableCollection<T> Items { get; private set; } = [];

    public JsonStore(string name) => file = Path.Combine(Settings.Dir, name);

    /// missing: what to start with when there's no file yet (null: empty). Returns whether that happened.
    public bool Load(Func<IEnumerable<T>?>? missing = null)
    {
        var created = false;
        try { Items = JsonSerializer.Deserialize<ObservableCollection<T>>(File.ReadAllText(file), Json) ?? []; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            Items = new(missing?.Invoke() ?? []);
            created = true;
        }
        catch (JsonException e)
        {
            var parked = $"{file}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(file, parked);
            Db.Error($"{Path.GetFileName(file)} unreadable, moved to {parked}", e);
            Items = [];
        }

        foreach (var n in Items) Watch(n);
        Items.CollectionChanged += (_, e) =>
        {
            foreach (T n in e.NewItems ?? Array.Empty<T>()) Watch(n);
            SaveSoon();
        };
        saveTimer.Tick += (_, _) => Flush();
        Application.Current.Exit += (_, _) => Flush();
        return created;
    }

    void Watch(T n)
    {
        n.PropertyChanged += (_, _) => SaveSoon();
        n.Items.CollectionChanged += (_, e) =>
        {
            foreach (TodoItem i in e.NewItems ?? Array.Empty<TodoItem>()) WatchItem(n, i);
            n.Touch();
        };
        foreach (var i in n.Items) WatchItem(n, i);
    }

    static void WatchItem(T n, TodoItem i) => i.PropertyChanged += (_, _) => n.Touch();

    public void SaveSoon()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    public void Flush()
    {
        saveTimer.Stop();
        try { Settings.WriteAtomic(file, JsonSerializer.Serialize(Items, Json)); }
        catch (IOException e)
        {
            Db.Error(Path.GetFileName(file) + " save", e);
            saveTimer.Start(); // retry; the data is still in memory
        }
    }
}

/// All notes, in notes.json.
public static class NoteStore
{
    static readonly JsonStore<Note> store = new("notes.json");
    static readonly string LegacyFile = Path.Combine(Settings.Dir, "notes.txt");

    public static ObservableCollection<Note> Notes => store.Items;

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
    }

    public static void SaveSoon() => store.SaveSoon();

    /// Notes and stickies: everything that can hold checklist items with due dates.
    public static IEnumerable<Note> Everything => Notes.Concat<Note>(Stickies.Items);

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
        from n in Everything
        from i in n.Items
        where i.Remind && !i.Done && !i.Reminded && i.Due <= now
        select (n, i);
}
