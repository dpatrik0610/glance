using System.IO;
using System.Text.Json;

namespace Glance.Services;

/// A node on the Board. X, Y and W are in board units (zoom 1 = pixels); height follows the content.
public class BoardItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// note | image | link | timer | stat
    public string Kind { get; set; } = "note";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 220;
    /// Optional name shown on top (notes, links, timers).
    public string Title { get; set; } = "";
    /// Note: Markdown. Link: URL, file or folder. Stat: which metric (BoardPage.Metrics).
    public string Text { get; set; } = "";
    /// Image file name in BoardStore.Files.
    public string? File { get; set; }
    /// Timer: its length; when running, when it ends; when paused, what's left (0 = not started).
    public double Seconds { get; set; } = 300;
    public DateTime? Ends { get; set; }
    public double Left { get; set; }
}

/// One board (a tab on the Board page): its nodes plus where the view was left (pan offset in screen pixels, zoom).
public class Board
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Board";
    public List<BoardItem> Items { get; set; } = [];
    public double X { get; set; } = 40;
    public double Y { get; set; } = 40;
    public double Zoom { get; set; } = 1;
}

/// All boards, in tab order, and the one last open.
public class Boards
{
    public List<Board> All { get; set; } = [];
    public string? Current { get; set; }
}

/// %APPDATA%\Glance\boards.json, pictures in board\ (named by node id, so shared by all boards). Local only.
public static class BoardStore
{
    public static readonly string Files = Path.Combine(Settings.Dir, "board");
    static readonly string File_ = Path.Combine(Settings.Dir, "boards.json");
    static readonly string Single = Path.Combine(Settings.Dir, "board.json"); // before tabs: one board

    public static Boards Load()
    {
        Boards b;
        try { b = JsonSerializer.Deserialize<Boards>(File.ReadAllText(File_)) ?? new(); }
        catch (FileNotFoundException)
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
        catch (IOException) { b = new(); }
        if (b.All.Count == 0) b.All.Add(new());
        return b;
    }

    public static void Save(Boards b) => Settings.WriteAtomic(File_, JsonSerializer.Serialize(b));

    /// "25" = 25 minutes, "1:30" = 1 min 30 s, "1:00:00" = an hour. Null when it isn't a positive length.
    public static double? ParseDuration(string s)
    {
        var parts = s.Trim().Split(':');
        if (parts.Length > 3 || parts.Any(p => !uint.TryParse(p, out _))) return null;
        var n = parts.Select(double.Parse).ToArray();
        var secs = n.Length switch { 1 => n[0] * 60, 2 => n[0] * 60 + n[1], _ => n[0] * 3600 + n[1] * 60 + n[2] };
        return secs > 0 ? secs : null;
    }

    public static string Clock(double secs)
    {
        var t = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, secs)));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }
}
