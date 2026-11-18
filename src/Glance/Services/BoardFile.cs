using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Glance.Services;

/// A board as one .glanceboard file (a zip: board.json and its pictures), to back up or move to another PC.
public static class BoardFile
{
    public const string Extension = ".glanceboard";

    /// pictures: the folder the board's pictures are in (BoardStore.Files).
    public static void Export(Board b, string path, string pictures)
    {
        File.Delete(path + ".tmp");
        using (var zip = ZipFile.Open(path + ".tmp", ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("board.json").Open()))
                w.Write(JsonSerializer.Serialize(b, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var f in b.Items.Select(i => i.File).OfType<string>().Distinct())
                if (File.Exists(Path.Combine(pictures, f))) zip.CreateEntryFromFile(Path.Combine(pictures, f), "pictures/" + f);
        }
        File.Move(path + ".tmp", path, true); // a half-written file never takes the name
    }

    /// A new board from a file: new ids throughout (so importing the same file twice gives two boards), pictures copied into `pictures`.
    public static Board Import(string path, string pictures)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("board.json") ?? throw new InvalidDataException("Not a Glance board file.");
        Board b;
        using (var r = new StreamReader(entry.Open())) b = JsonSerializer.Deserialize<Board>(r.ReadToEnd()) ?? throw new InvalidDataException("Empty board file.");

        b.Id = Guid.NewGuid().ToString("N");
        var ids = new Dictionary<string, string>();
        foreach (var it in b.Items)
        {
            ids[it.Id] = it.Id = Guid.NewGuid().ToString("N");
            if (it.File is null) continue;
            var picture = zip.GetEntry("pictures/" + it.File);
            it.File = it.Id + Path.GetExtension(it.File).ToLowerInvariant();
            if (picture is null) continue; // shows as "Picture missing"
            Directory.CreateDirectory(pictures);
            picture.ExtractToFile(Path.Combine(pictures, it.File), true);
        }
        b.Edges.RemoveAll(e => !ids.ContainsKey(e.From) || !ids.ContainsKey(e.To));
        foreach (var e in b.Edges) (e.Id, e.From, e.To) = (Guid.NewGuid().ToString("N"), ids[e.From], ids[e.To]);
        b.Items.RemoveAll(i => i.Kind == "timer");
        return b;
    }
}
