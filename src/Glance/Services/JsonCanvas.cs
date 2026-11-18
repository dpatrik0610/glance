using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace Glance.Services;

/// Boards as JSON Canvas (jsoncanvas.org): the open format Obsidian's Canvas saves, so boards move both ways.
public static class JsonCanvas
{
    static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg"];
    const double Pad = 26; // a Glance card's padding + border around its content width

    /// bounds: each node's box as drawn (null: from its saved size). Pictures are copied to a "<name> files" folder beside it.
    public static void Export(Board b, Func<BoardItem, Rect>? bounds, string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var media = Path.GetFileNameWithoutExtension(path) + " files";
        var nodes = new JsonArray();
        var rows = new Dictionary<string, int>();
        foreach (var it in b.Items)
        {
            var r = bounds?.Invoke(it) ?? new Rect(it.X, it.Y, it.W + Pad, it.H > 0 ? it.H : 120);
            if (bounds is null && b.Layout == "kanban") // columns side by side, cards stacked
            {
                var col = b.Columns.Contains(it.Column) ? it.Column : b.Columns.FirstOrDefault() ?? "";
                var row = rows[col] = rows.GetValueOrDefault(col) + 1;
                r = new Rect(Math.Max(0, b.Columns.IndexOf(col)) * 300, row * 150, 260, 130);
            }
            var n = new JsonObject { ["id"] = it.Id, ["x"] = Math.Round(r.X), ["y"] = Math.Round(r.Y), ["width"] = Math.Round(r.Width), ["height"] = Math.Round(r.Height) };
            if (it.Color != "") n["color"] = it.Color;
            switch (it.Kind)
            {
                case "image" when it.File is not null:
                    var src = Path.Combine(BoardStore.Files, it.File);
                    if (File.Exists(src))
                    {
                        Directory.CreateDirectory(Path.Combine(dir, media));
                        File.Copy(src, Path.Combine(dir, media, it.File), true);
                    }
                    n["type"] = "file";
                    n["file"] = $"{media}/{it.File}";
                    break;
                case "link":
                    var t = it.Text.Trim().Trim('"');
                    if (Pages.BoardPage.WebAddress(t) is { } u) { n["type"] = "link"; n["url"] = u.AbsoluteUri; }
                    else { n["type"] = "file"; n["file"] = t; }
                    break;
                case "group":
                    n["type"] = "group";
                    if (it.Title.Trim() != "") n["label"] = it.Title;
                    break;
                case "embed":
                    var note = NoteStore.Notes.FirstOrDefault(x => x.Id == it.Text);
                    n["type"] = "text";
                    n["text"] = note is null ? "(deleted note)"
                        : $"# {note.DisplayTitle}\n\n" + (note.Kind == NoteKind.Text ? note.Text : string.Join("\n", note.Items.Select(i => $"- [{(i.Done ? 'x' : ' ')}] {i.Text}")));
                    break;
                case "stat":
                    n["type"] = "text";
                    n["text"] = $"Live {it.Text} (Glance)";
                    break;
                default:
                    n["type"] = "text";
                    n["text"] = (it.Title.Trim() != "" ? $"# {it.Title}\n\n" : "") + it.Text;
                    break;
            }
            nodes.Add(n);
        }
        var ids = b.Items.Select(i => i.Id).ToHashSet();
        var edges = new JsonArray();
        foreach (var e in b.Edges.Where(e => ids.Contains(e.From) && ids.Contains(e.To)))
        {
            var j = new JsonObject { ["id"] = e.Id, ["fromNode"] = e.From, ["toNode"] = e.To, ["toEnd"] = "arrow" };
            if (e.Label != "") j["label"] = e.Label;
            edges.Add(j);
        }
        File.WriteAllText(path, new JsonObject { ["nodes"] = nodes, ["edges"] = edges }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// A new board (named after the file). Pictures found on disk are copied into the board's own folder.
    public static Board Import(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new FormatException("Not a canvas file.");
        var b = new Board { Name = Path.GetFileNameWithoutExtension(path) };
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var n in root["nodes"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            double Num(string k) => n[k]?.GetValue<double>() ?? 0;
            string Str(string k) => n[k]?.GetValue<string>() ?? "";
            var it = new BoardItem { Id = Str("id") is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"), X = Num("x"), Y = Num("y"), Color = Str("color") };
            var w = Num("width");
            it.W = Math.Max(120, w - Pad);
            switch (Str("type"))
            {
                case "group":
                    (it.Kind, it.Title, it.W, it.H) = ("group", Str("label"), Math.Max(120, w - Pad), Math.Max(80, Num("height")));
                    break;
                case "link":
                    (it.Kind, it.Text) = ("link", Str("url"));
                    break;
                case "file":
                    var file = Str("file");
                    if (Pictures.Contains(Path.GetExtension(file).ToLowerInvariant()) && Find(dir, file) is { } found)
                    {
                        Directory.CreateDirectory(BoardStore.Files);
                        it.Kind = "image";
                        it.File = it.Id + Path.GetExtension(found).ToLowerInvariant();
                        File.Copy(found, Path.Combine(BoardStore.Files, it.File), true);
                    }
                    else (it.Kind, it.Text) = ("link", Find(dir, file) ?? file);
                    break;
                default: // text, and anything newer we don't know
                    var text = Str("text");
                    // "# Title" then a blank line: the title (as Export writes it).
                    if (text.StartsWith("# ") && text.IndexOf("\n\n", StringComparison.Ordinal) is var cut and > 2 && !text[..cut].Contains('\n'))
                        (it.Title, text) = (text[2..cut].Trim(), text[(cut + 2)..]);
                    it.Text = text;
                    break;
            }
            b.Items.Add(it);
        }
        var ids = b.Items.Select(i => i.Id).ToHashSet();
        foreach (var e in root["edges"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var (from, to) = (e["fromNode"]?.GetValue<string>() ?? "", e["toNode"]?.GetValue<string>() ?? "");
            if (ids.Contains(from) && ids.Contains(to))
                b.Edges.Add(new() { From = from, To = to, Label = e["label"]?.GetValue<string>() ?? "" });
        }
        // Groups behind everything else.
        b.Items = [.. b.Items.OrderBy(i => i.Kind == "group" ? 0 : 1)];
        return b;
    }

    /// Obsidian writes paths from the vault's root, which may be a few folders above the canvas file.
    static string? Find(string dir, string file)
    {
        if (Path.IsPathFullyQualified(file)) return File.Exists(file) || Directory.Exists(file) ? file : null;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, file.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }
        return null;
    }
}
