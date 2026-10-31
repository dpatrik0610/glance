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
    bool done, remind;
    DateTime? due;

    public string Text { get => text; set => Set(ref text, value); }
    public bool Done { get => done; set { if (Set(ref done, value)) Raise(nameof(IsOverdue)); } }
    public DateTime? Due { get => due; set { if (Set(ref due, value)) { Raise(nameof(DueText)); Raise(nameof(IsOverdue)); } } }
    public bool Remind { get => remind; set => Set(ref remind, value); }
    /// Set once the reminder has shown, so it fires once (also across restarts).
    public bool Reminded { get; set; }

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
    string title = "", text = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonConverter(typeof(JsonStringEnumConverter))] public NoteKind Kind { get; set; }
    public string Title { get => title; set { if (Set(ref title, value)) Raise(nameof(DisplayTitle)); } }
    public string Text { get => text; set { if (Set(ref text, value)) Raise(nameof(Summary)); } }
    public ObservableCollection<TodoItem> Items { get; set; } = [];

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

/// All notes, persisted to %AppData%\Glance\notes.json. Any edit schedules a debounced save; exit flushes.
public static class NoteStore
{
    static readonly string File_ = Path.Combine(Settings.Dir, "notes.json");
    static readonly string LegacyFile = Path.Combine(Settings.Dir, "notes.txt");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };

    public static ObservableCollection<Note> Notes { get; private set; } = [];

    /// Raised when something (a reminder click) wants a note shown.
    public static event Action<Note>? FocusRequested;
    public static void RequestFocus(Note n) => FocusRequested?.Invoke(n);

    public static void Load()
    {
        var migrated = false;
        try { Notes = JsonSerializer.Deserialize<ObservableCollection<Note>>(File.ReadAllText(File_), Json) ?? []; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // First run of multi-note: import the old single pad.
            if (File.Exists(LegacyFile))
            {
                Notes = [new Note { Title = "Notes", Text = File.ReadAllText(LegacyFile) }];
                migrated = true;
            }
        }
        catch (JsonException e)
        {
            // Never overwrite a file we couldn't read: park it next to the new one.
            var parked = $"{File_}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(File_, parked);
            Db.Error($"notes.json unreadable, moved to {parked}", e);
            Notes = [];
        }

        foreach (var n in Notes) Watch(n);
        Notes.CollectionChanged += (_, e) =>
        {
            foreach (Note n in e.NewItems ?? Array.Empty<Note>()) Watch(n);
            SaveSoon();
        };
        saveTimer.Tick += (_, _) => Flush();
        Application.Current.Exit += (_, _) => Flush();

        if (migrated)
        {
            Flush();
            File.Move(LegacyFile, LegacyFile + ".imported", true);
        }
    }

    static void Watch(Note n)
    {
        n.PropertyChanged += (_, _) => SaveSoon();
        n.Items.CollectionChanged += (_, e) =>
        {
            foreach (TodoItem i in e.NewItems ?? Array.Empty<TodoItem>()) WatchItem(n, i);
            n.Touch();
        };
        foreach (var i in n.Items) WatchItem(n, i);
    }

    static void WatchItem(Note n, TodoItem i) => i.PropertyChanged += (_, _) => n.Touch();

    public static void SaveSoon()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    public static void Flush()
    {
        saveTimer.Stop();
        try { Settings.WriteAtomic(File_, JsonSerializer.Serialize(Notes, Json)); }
        catch (IOException e)
        {
            Db.Error("notes save", e);
            saveTimer.Start(); // retry; the data is still in memory
        }
    }

    /// Items whose reminder is due now and hasn't shown yet.
    public static IEnumerable<(Note Note, TodoItem Item)> DueReminders(DateTime now) =>
        from n in Notes
        from i in n.Items
        where i.Remind && !i.Done && !i.Reminded && i.Due <= now
        select (n, i);
}
