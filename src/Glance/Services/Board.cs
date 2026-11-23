using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace Glance.Services;

/// A node on a board. X, Y, W, H are in board units (zoom 1 = pixels); H is 0 for "follows the content" (groups set it).
public class BoardItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// note | image | link | stat | group | embed
    public string Kind { get; set; } = "note";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 220;
    public double H { get; set; }
    /// Optional name shown on top (notes, links, groups).
    public string Title { get; set; } = "";
    /// Note: Markdown. Link: URL, file or folder. Stat: which metric. Embed: the Notes-page note's id.
    public string Text { get; set; } = "";
    /// Image file name in BoardStore.Files.
    public string? File { get; set; }
    /// "" or "1".."6": red, orange, yellow, green, cyan, purple (JSON Canvas's preset colors).
    public string Color { get; set; } = "";
    /// Can't be dragged or resized.
    public bool Locked { get; set; }
    /// Kanban layout: the column it's in (an unknown or empty one means the first).
    public string Column { get; set; } = "";
}

/// An arrow from one node to another.
public class BoardEdge
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Label { get; set; } = "";
}

/// One board (a tab on the Board page): its nodes, arrows and drawing, plus where the view was left.
public class Board
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Board";
    public List<BoardItem> Items { get; set; } = [];
    public List<BoardEdge> Edges { get; set; } = [];
    /// The pen layer, as ISF (Ink Serialized Format) in base64.
    public string Ink { get; set; } = "";
    /// dots | grid | plain
    public string Background { get; set; } = "dots";
    /// canvas | kanban
    public string Layout { get; set; } = "canvas";
    public List<string> Columns { get; set; } = ["To do", "Doing", "Done"];
    /// Pan offset in screen pixels, and zoom.
    public double X { get; set; } = 40;
    public double Y { get; set; } = 40;
    public double Zoom { get; set; } = 1;
}

/// All boards, in tab order, and the one last open.
public class Boards
{
    public List<Board> All { get; set; } = [];
    public string? Current { get; set; }
    /// Board tasks whose due reminder has shown (BoardStore.TaskKey), so each shows once.
    public List<string> Reminded { get; set; } = [];
}

/// A "- [ ] task" line in a board note.
public record BoardTask(Board Board, BoardItem Item, int Ordinal, string Text, bool Done, DateTime? Due);

/// %APPDATA%\Glance\boards.json, pictures in board\ (named by node id, so shared by all boards). Local only.
/// One copy in memory for the Board page, the Tasks page and reminders; saved shortly after each change.
public static partial class BoardStore
{
    public static readonly string Files = Path.Combine(Settings.Dir, "board");
    static readonly string File_ = Path.Combine(Settings.Dir, "boards.json");
    static readonly string Single = Path.Combine(Settings.Dir, "board.json"); // before tabs: one board
    static Boards? data;
    static DispatcherTimer? saveTimer;
    static bool readOnly; // the file exists but couldn't be read: never save over it

    public static Boards Data => data ??= Open();

    /// A node changed outside the Board page (a task ticked on the Tasks page): the page redraws it.
    public static event Action<BoardItem>? Edited;

    static Boards Open()
    {
        var b = Load(out var unreadable);
        if (unreadable is null) return b;
        // The file is there but can't be read (locked by a sync tool or antivirus, no access). The empty stand-in
        // is never saved this session: that save would replace the real boards with it.
        readOnly = true;
        Db.Error("board load", unreadable);
        Application.Current?.Dispatcher.BeginInvoke(() => Dialog.Info("Board", "Couldn't read your boards",
            $"{unreadable.Message}\n\nThe file was left untouched. Changes on the Board won't be saved until Glance restarts."));
        return b;
    }

    /// unreadable: boards.json exists but couldn't be read; what's returned is then an empty stand-in.
    internal static Boards Load(out Exception? unreadable)
    {
        Boards b;
        unreadable = null;
        var read = false; // only a cleanly read file says which pictures are unused
        try { b = JsonSerializer.Deserialize<Boards>(File.ReadAllText(File_)) ?? new(); read = true; }
        catch (Exception none) when (none is FileNotFoundException or DirectoryNotFoundException) // first run, or from before tabs
        {
            b = new();
            try { if (JsonSerializer.Deserialize<Board>(File.ReadAllText(Single)) is { } old) b.All.Add(old); }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        catch (JsonException)
        {
            try { File.Copy(File_, File_ + ".bad", true); } catch (IOException) { } // kept, since the next save writes over it
            b = new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            b = new();
            unreadable = e;
        }
        if (b.All.Count == 0) b.All.Add(new());
        foreach (var board in b.All) board.Items.RemoveAll(i => i.Kind == "timer"); // timers moved to their own page
        if (read && b.All.Count > 0) Sweep(b);
        return b;
    }

    /// Pictures no node uses any more (deleted nodes keep theirs for undo until the next start).
    static void Sweep(Boards b)
    {
        var used = b.All.SelectMany(x => x.Items).Select(i => i.File).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(Files))
                if (!used.Contains(Path.GetFileName(f))) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // none yet, or in use: next time
    }

    public static void Save()
    {
        saveTimer?.Stop();
        if (data is not null && !readOnly) Settings.WriteAtomic(File_, JsonSerializer.Serialize(data));
    }

    public static void SaveSoon()
    {
        if (saveTimer is null)
        {
            saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
            saveTimer.Tick += (_, _) => Save();
            Application.Current.Exit += (_, _) => { if (saveTimer.IsEnabled) Save(); };
        }
        saveTimer.Stop();
        saveTimer.Start();
    }

    public static void Changed(BoardItem it)
    {
        SaveSoon();
        Edited?.Invoke(it);
    }

    // ---- tasks in board notes: "- [ ] call mom @2026-11-20 17:00" ----

    [GeneratedRegex(@"\s@(\d{4}-\d{2}-\d{2} \d{2}:\d{2})\s*$")]
    private static partial Regex DueTag();

    [GeneratedRegex(@"^(\s*[-*+]\s+\[[ xX]\]\s.*?)\s@([^@\r\n]+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex LooseDue();

    /// Every task in every board's notes.
    public static IEnumerable<BoardTask> Tasks() =>
        from b in Data.All
        from it in b.Items
        where it.Kind == "note"
        from t in MarkdownView.TaskLines(it.Text)
        let due = DueTag().Match(t.Text)
        select new BoardTask(b, it, t.Ordinal, due.Success ? t.Text[..due.Index].Trim() : t.Text.Trim(), t.Done,
            due.Success ? DateTime.ParseExact(due.Groups[1].Value, "yyyy-MM-dd HH:mm", null) : null);

    /// Turns typed due dates on task lines ("@tomorrow 9", "@fri 14:00") into fixed ones, so they don't move every day.
    public static string FixDues(string text, DateTime now) => LooseDue().Replace(text, m =>
    {
        if (DueTag().IsMatch(m.Value)) return m.Value;
        return Due.Parse(m.Groups[2].Value, now) is { } d ? $"{m.Groups[1].Value} @{d:yyyy-MM-dd HH:mm}" : m.Value;
    });

    /// Identifies a task's reminder: changing its text or time re-arms it.
    public static string TaskKey(BoardTask t) => $"{t.Item.Id}|{t.Text}|{t.Due:yyyy-MM-dd HH:mm}";

    /// Board tasks due now whose reminder hasn't shown yet; marks them shown.
    public static List<BoardTask> DueReminders(DateTime now)
    {
        var due = Tasks().Where(t => !t.Done && t.Due <= now && !Data.Reminded.Contains(TaskKey(t))).ToList();
        if (due.Count == 0) return due;
        Data.Reminded.AddRange(due.Select(TaskKey));
        if (Data.Reminded.Count > 500) Data.Reminded.RemoveRange(0, Data.Reminded.Count - 500);
        SaveSoon();
        return due;
    }
}
