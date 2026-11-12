using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Glance.Services;

/// A run of text in a diff line. Kind: ' ' kept, '-' removed, '+' added.
public record Seg(char Kind, string Text);

/// One line of a text diff. Kind: ' ' same, '-' removed, '+' added, '@' a gap between hunks.
/// Segs: an edited line's unchanged start and end are ' ', the changed middle carries the line's kind.
public record DiffLine(char Kind, int? Left, int? Right, IReadOnlyList<Seg> Segs);

/// A file that isn't the same in both folders. Kind: '<' only left, '>' only right, '≠' differs, '!' couldn't be read.
public record FolderItem(string Path, char Kind, FileInfo? L, FileInfo? R)
{
    public string KindText => Kind switch { '<' => "only left", '>' => "only right", '≠' => "differs", _ => "unreadable" };
    public string LeftText => L is null ? "" : $"{Compare.Size(L.Length)} · {L.LastWriteTime:yyyy-MM-dd HH:mm}";
    public string RightText => R is null ? "" : $"{Compare.Size(R.Length)} · {R.LastWriteTime:yyyy-MM-dd HH:mm}";
    public bool CanDiff => Kind == '≠';
}

public record DupFile(string Path, DateTime Modified);

/// Files with the same content. Oldest first.
public record DupGroup(long Size, List<DupFile> Files)
{
    public long Wasted => Size * (Files.Count - 1);
}

public static class Compare
{
    // Hidden/system files stay out (.git, desktop.ini, thumbs.db); links and junctions aren't followed, so no loops.
    static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    public static string Size(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1 << 20 => $"{b / (double)(1 << 20):0.0} MB",
        >= 1 << 10 => $"{b / 1024.0:0} KB",
        _ => $"{b} B",
    };

    /// Offset of the first byte that differs (a shorter file differs where it ends), or -1 when identical.
    public static long FirstDifference(string a, string b, CancellationToken ct = default)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        var ba = new byte[1 << 16];
        var bb = new byte[1 << 16];
        for (long pos = 0; ; pos += ba.Length)
        {
            ct.ThrowIfCancellationRequested();
            int na = fa.ReadAtLeast(ba, ba.Length, false), nb = fb.ReadAtLeast(bb, bb.Length, false);
            var common = ba.AsSpan(0, na).CommonPrefixLength(bb.AsSpan(0, nb));
            if (common < Math.Max(na, nb)) return pos + common;
            if (na < ba.Length) return -1;
        }
    }

    /// Git's own test: a NUL byte in the first 8000 bytes means binary.
    public static bool IsText(string path)
    {
        using var f = File.OpenRead(path);
        var buf = new byte[8000];
        return !buf.AsSpan(0, f.ReadAtLeast(buf, buf.Length, false)).Contains((byte)0);
    }

    /// Line diff by git, with the changed part of each edited line marked. Line-ending differences are ignored.
    public static async Task<List<DiffLine>> Diff(string a, string b)
    {
        var r = await Shell.Run("git", "diff", "--no-index", "--no-color", "--no-ext-diff", "--ignore-cr-at-eol", "--", a, b);
        if (r.Code > 1) throw new IOException(r.Err.Trim()); // 0 = same, 1 = differ
        return ParseUnified(r.Out);
    }

    static readonly Regex Hunk = new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)");

    public static List<DiffLine> ParseUnified(string diff)
    {
        var lines = new List<DiffLine>();
        int left = 0, right = 0;
        var inHunk = false;
        foreach (var raw in diff.Split('\n'))
        {
            var s = raw.TrimEnd('\r');
            if (Hunk.Match(s) is { Success: true } h)
            {
                left = int.Parse(h.Groups[1].Value);
                right = int.Parse(h.Groups[2].Value);
                if (inHunk || left > 1 || right > 1) lines.Add(new('@', null, null, []));
                inHunk = true;
            }
            else if (!inHunk || s.Length == 0) continue; // the diff/index/---/+++ header
            else if (s[0] == ' ') lines.Add(new(' ', left++, right++, [new(' ', s[1..])]));
            else if (s[0] == '-') lines.Add(new('-', left++, null, [new('-', s[1..])]));
            else if (s[0] == '+') lines.Add(new('+', null, right++, [new('+', s[1..])]));
            // "\ No newline at end of file" is skipped
        }
        MarkEdits(lines);
        return lines;
    }

    // In each run of removed lines followed by added ones, an added line that resembles a removed one (in order) is
    // that line edited: both get their common start and end unmarked, so only the changed middle stands out.
    static void MarkEdits(List<DiffLine> lines)
    {
        for (var i = 0; i < lines.Count;)
        {
            var del = i;
            while (i < lines.Count && lines[i].Kind == '-') i++;
            var add = i;
            while (i < lines.Count && lines[i].Kind == '+') i++;
            if (add == del || i == add) { i = Math.Max(i, del + 1); continue; }
            var next = add;
            for (var d = del; d < add && next < i; d++)
                for (var a = next; a < i; a++)
                {
                    var (x, y) = (lines[d].Segs[0].Text, lines[a].Segs[0].Text);
                    var pre = x.AsSpan().CommonPrefixLength(y);
                    var suf = Math.Min(Math.Min(x.Length, y.Length) - pre, CommonSuffix(x, y));
                    if (pre + suf < 0.4 * Math.Max(x.Length, y.Length)) continue;
                    lines[d] = lines[d] with { Segs = Split(x, pre, suf, '-') };
                    lines[a] = lines[a] with { Segs = Split(y, pre, suf, '+') };
                    next = a + 1;
                    break;
                }
        }
    }

    static int CommonSuffix(string x, string y)
    {
        var n = 0;
        while (n < x.Length && n < y.Length && x[^(n + 1)] == y[^(n + 1)]) n++;
        return n;
    }

    static List<Seg> Split(string s, int pre, int suf, char kind) =>
        new[] { new Seg(' ', s[..pre]), new Seg(kind, s[pre..^suf]), new Seg(' ', s[^suf..]) }.Where(g => g.Text.Length > 0).ToList();

    /// Files that aren't the same in both trees (by relative path; same size → byte compare), and how many are.
    public static (List<FolderItem> Items, int Same) Folders(string a, string b, CancellationToken ct)
    {
        Dictionary<string, FileInfo> Files(string root) => new DirectoryInfo(root).EnumerateFiles("*", Walk)
            .ToDictionary(f => Path.GetRelativePath(root, f.FullName), StringComparer.OrdinalIgnoreCase);
        var left = Files(a);
        var right = Files(b);
        var items = new List<FolderItem>();
        var same = 0;
        foreach (var (rel, l) in left)
        {
            ct.ThrowIfCancellationRequested();
            if (!right.TryGetValue(rel, out var r)) { items.Add(new(rel, '<', l, null)); continue; }
            try
            {
                if (l.Length != r.Length || FirstDifference(l.FullName, r.FullName, ct) >= 0) items.Add(new(rel, '≠', l, r));
                else same++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { items.Add(new(rel, '!', l, r)); }
        }
        items.AddRange(right.Where(kv => !left.ContainsKey(kv.Key)).Select(kv => new FolderItem(kv.Key, '>', null, kv.Value)));
        return (items.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList(), same);
    }

    /// Groups of identical files under the roots, most wasted space first. Only same-size files get hashed.
    public static List<DupGroup> Duplicates(IEnumerable<string> roots, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        // Overlapping roots (C:\a and C:\a\b) must not make a file its own duplicate: recycling one would lose the only copy.
        var bySize = roots.SelectMany(r => new DirectoryInfo(r).EnumerateFiles("*", Walk))
            .Where(f => f.Length > 0) // every empty file is "the same"; not worth listing
            .DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .GroupBy(f => f.Length).Where(g => g.Skip(1).Any()).ToList();
        int total = bySize.Sum(g => g.Count()), done = 0;
        var groups = new List<DupGroup>();
        foreach (var size in bySize)
        {
            var byHash = size.GroupBy(f =>
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((++done, total));
                return Hash(f.FullName);
            });
            groups.AddRange(byHash.Where(g => g.Key is not null && g.Skip(1).Any())
                .Select(g => new DupGroup(size.Key, g.OrderBy(f => f.LastWriteTimeUtc).Select(f => new DupFile(f.FullName, f.LastWriteTime)).ToList())));
        }
        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    static string? Hash(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(f));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; } // locked or gone: not listed
    }

    /// Still a copy of `keep`, byte for byte, right now (either may have changed or gone since the scan).
    public static bool StillDuplicate(string victim, string keep) =>
        victim != keep && File.Exists(victim) && File.Exists(keep) && FirstDifference(victim, keep) < 0;

    /// To the Recycle Bin, with Explorer's own prompts: where there is no bin (network, too big) Windows asks before deleting for good.
    /// False when the user said no.
    public static bool Recycle(string path)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }
}
