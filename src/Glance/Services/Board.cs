using System.IO;
using System.Text.Json;

namespace Glance.Services;

/// A note or picture on the Board. X, Y and W are in board units (zoom 1 = pixels).
public class BoardItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "note"; // note | image
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 220;
    public string Text { get; set; } = "";
    /// Image file name in BoardStore.Files.
    public string? File { get; set; }
}

/// The items plus where the view was left (pan offset in screen pixels, zoom).
public class Board
{
    public List<BoardItem> Items { get; set; } = [];
    public double X { get; set; } = 40;
    public double Y { get; set; } = 40;
    public double Zoom { get; set; } = 1;
}

/// %APPDATA%\Glance\board.json, pictures in board\. Local only.
public static class BoardStore
{
    public static readonly string Files = Path.Combine(Settings.Dir, "board");
    static readonly string File_ = Path.Combine(Settings.Dir, "board.json");

    public static Board Load()
    {
        try { return JsonSerializer.Deserialize<Board>(File.ReadAllText(File_)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public static void Save(Board b) => Settings.WriteAtomic(File_, JsonSerializer.Serialize(b));
}
