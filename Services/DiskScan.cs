using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using Microsoft.Win32;

namespace Glance.Services;

/// A folder or file in a scan. Kids == null: a file, or a folder's small files lumped into one entry.
public sealed class Node
{
    public string Name = "";
    public long Size;
    public Node? Parent;
    public List<Node>? Kids;
    /// The folder's files under DiskScan.SmallFile, as one entry: nothing to reveal or recycle.
    public bool Lump;

    public bool IsDir => Kids is not null;
    public string Path => Parent is null ? Name : System.IO.Path.Combine(Parent.Path, Name);
}

/// Where a drive's space went: WinDirStat's scan when it is installed, Glance's own parallel walk otherwise.
public static class DiskScan
{
    // ponytail: files under 1 MB are one "small files" entry per folder, so a million-file drive is a few MB of tree and the
    // treemap stays fast. Keep every file (and virtualize the list) if single small files ever need to be seen.
    public const long SmallFile = 1 << 20;
    public const string LumpName = "small files";

    public sealed class Progress { public long Files, Bytes; }

    // Reparse points (junctions, symlinks) are never entered: they'd count another place's bytes, or loop.
    static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// Glance's own scan: every directory is read once, straight from its directory records, subfolders in parallel.
    public static Node Walk(string root, Progress? progress = null, CancellationToken ct = default)
    {
        var node = new Node { Name = root, Kids = [] };
        Fill(node, root, progress, ct);
        return node;
    }

    static void Fill(Node dir, string path, Progress? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long small = 0, bytes = 0;
        var files = 0;
        List<Node>? subs = null;
        try
        {
            // No name string for a small file: it is only ever a number.
            foreach (var (name, isDir, length) in new FileSystemEnumerable<(string?, bool, long)>(path,
                (ref FileSystemEntry e) => (e.IsDirectory || e.Length >= SmallFile ? e.FileName.ToString() : null, e.IsDirectory, e.Length), Flat))
            {
                if (isDir) { (subs ??= []).Add(new Node { Name = name!, Parent = dir, Kids = [] }); continue; }
                files++;
                bytes += length;
                if (name is null) small += length;
                else dir.Kids!.Add(new Node { Name = name, Size = length, Parent = dir });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // gone or locked mid-scan: counts what was read
        if (progress is not null)
        {
            Interlocked.Add(ref progress.Files, files);
            Interlocked.Add(ref progress.Bytes, bytes);
        }
        if (subs is not null)
        {
            Parallel.ForEach(subs, new ParallelOptions { CancellationToken = ct }, s => Fill(s, Path.Combine(path, s.Name), progress, ct));
            dir.Kids!.AddRange(subs);
        }
        if (small > 0) dir.Kids!.Add(new Node { Name = LumpName, Size = small, Parent = dir, Lump = true });
        dir.Size = bytes + (subs?.Sum(s => s.Size) ?? 0);
        Sort(dir);
    }

    static void Sort(Node dir) => dir.Kids!.Sort((a, b) => b.Size.CompareTo(a.Size));

    /// WinDirStat.exe (2.6 or newer, for /SaveTo), or null.
    public static string? WinDirStat()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\windirstat.exe");
        var exe = (key?.GetValue("") as string
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinDirStat", "WinDirStat.exe")).Trim('"');
        if (!File.Exists(exe)) return null;
        var v = FileVersionInfo.GetVersionInfo(exe);
        return v.FileMajorPart > 2 || (v.FileMajorPart == 2 && v.FileMinorPart >= 6) ? exe : null;
    }

    /// WinDirStat scans `root` without a window and saves a CSV, which becomes the tree. Elevated (one UAC prompt), it reads
    /// NTFS's file table instead of walking folders. Null: declined at the UAC prompt, or no report came out.
    public static async Task<Node?> ViaWinDirStat(string exe, string root, bool elevated, CancellationToken ct = default)
    {
        var csv = Path.Combine(Path.GetTempPath(), $"glance-scan-{Environment.ProcessId}.csv");
        File.Delete(csv);
        try
        {
            // A drive root's closing backslash would escape the closing quote ("C:\" arrives as C:"), so it is doubled.
            var start = new ProcessStartInfo(exe, $"/SaveTo \"{csv}\" \"{(root.EndsWith('\\') ? root + "\\" : root)}\"")
                { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (elevated) start.Verb = "runas";
            using var p = Process.Start(start);
            if (p is null) return null;
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                throw;
            }
            // Its exit code is nonzero for an incomplete scan too (folders it couldn't open), with the report still written.
            return File.Exists(csv) ? await Task.Run(() => Parse(File.ReadLines(csv)), ct) : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; } // UAC prompt declined
        finally { File.Delete(csv); }
    }

    /// WinDirStat's scan CSV: a header, then one row per item, parents before children, depth first:
    /// "path",files,folders,logical,physical,"attributes",changed,type flags,index. Read by position: the header is translated
    /// in other interface languages. Sizes are the physical ones (what the item takes on disk).
    public static Node? Parse(IEnumerable<string> lines)
    {
        Node? root = null;
        var open = new Stack<(Node Node, string Path)>(); // the row's ancestors
        var lumps = new Dictionary<Node, Node>();
        foreach (var line in lines.Skip(1))
        {
            var end = line.IndexOf("\",", StringComparison.Ordinal); // a Windows name has no quote in it
            if (end < 1 || line[0] != '"') continue;
            var path = line[1..end].TrimEnd('\\');
            var f = line[(end + 2)..].Split(',');
            if (f.Length < 7 || !long.TryParse(f[3], out var size)
                || !int.TryParse(f[6].AsSpan(f[6].StartsWith("0x") ? 2 : 0), System.Globalization.NumberStyles.HexNumber, null, out var flags)) continue;
            var isDir = (flags & 4) != 0;

            if (root is null)
            {
                root = new Node { Name = line[1..end], Size = size, Kids = [] };
                open.Push((root, path));
                continue;
            }
            var cut = path.LastIndexOf('\\');
            if (cut < 0) continue;
            var parentPath = path.AsSpan(0, cut);
            while (open.Count > 1 && !parentPath.Equals(open.Peek().Path, StringComparison.OrdinalIgnoreCase)) open.Pop();
            var parent = open.Peek().Node;
            if (!isDir && size < SmallFile)
            {
                if (!lumps.TryGetValue(parent, out var lump))
                    parent.Kids!.Add(lumps[parent] = lump = new Node { Name = LumpName, Parent = parent, Lump = true });
                lump.Size += size;
                continue;
            }
            var node = new Node { Name = path[(cut + 1)..], Size = size, Parent = parent, Kids = isDir ? [] : null };
            parent.Kids!.Add(node);
            if (isDir) open.Push((node, path));
        }
        if (root is not null) SortAll(root);
        return root;
    }

    static void SortAll(Node dir)
    {
        Sort(dir);
        foreach (var k in dir.Kids!) if (k.IsDir) SortAll(k);
    }

    public const string SmallKind = "Small files", OtherKind = "Other";

    static readonly Dictionary<string, string> KindOf = new (string Kind, string Exts)[]
    {
        ("Video", "mp4 mkv avi mov wmv webm m4v flv mpg mpeg ts m2ts"),
        ("Images", "jpg jpeg png gif bmp webp heic tif tiff psd raw cr2 nef dng svg ico"),
        ("Audio", "mp3 flac wav ogg m4a aac wma opus"),
        ("Archives and disk images", "zip rar 7z tar gz tgz bz2 xz zst iso img vhd vhdx vmdk wim esd cab"),
        ("Apps and libraries", "exe dll msi msix appx sys so lib pak bin node pdb jar ocx"),
        ("Documents", "pdf doc docx xls xlsx ppt pptx txt md epub csv json xml odt ods"),
        ("Databases and logs", "db sqlite mdf ldf log etl evtx edb"),
    }.SelectMany(k => k.Exts.Split(' ').Select(e => (Ext: "." + e, k.Kind))).ToDictionary(x => x.Ext, x => x.Kind, StringComparer.OrdinalIgnoreCase);

    /// What the bytes under dir are, by file type, biggest first. The lumped small files are one kind of their own.
    public static List<(string Kind, long Bytes)> Kinds(Node dir)
    {
        var sum = new Dictionary<string, long>();
        var todo = new Stack<Node>();
        todo.Push(dir);
        while (todo.TryPop(out var n))
        {
            if (n.IsDir) { foreach (var k in n.Kids!) todo.Push(k); continue; }
            var kind = n.Lump ? SmallKind : KindOf.GetValueOrDefault(System.IO.Path.GetExtension(n.Name), OtherKind);
            sum[kind] = sum.GetValueOrDefault(kind) + n.Size;
        }
        return sum.Where(s => s.Value > 0).OrderByDescending(s => s.Value).Select(s => (s.Key, s.Value)).ToList();
    }

    /// Takes a recycled item out of the tree; the folders above it shrink and re-sort.
    public static void Remove(Node node)
    {
        if (node.Parent is not { } gone) return;
        gone.Kids!.Remove(node);
        for (var p = gone; p is not null; p = p.Parent)
        {
            p.Size -= node.Size;
            if (p.Parent is not null) Sort(p.Parent);
        }
    }
}
