using System.IO;

namespace Glance.Services;

public record Repo(string Name, string Path, string Branch, int Ahead, int Behind, bool Tracked, int Changes,
    long When, string Ago, string Subject, string? Web)
{
    public bool Dirty => Changes > 0;
    public bool HasWeb => Web is not null;
    public bool OutOfSync => Ahead + Behind > 0;
    public string ChangesText => Changes == 0 ? "clean" : $"{Changes} changed";
    public string Sync => !Tracked ? "local only"
        : !OutOfSync ? "up to date"
        : string.Join(" ", new[] { Ahead > 0 ? $"↑{Ahead}" : "", Behind > 0 ? $"↓{Behind}" : "" }.Where(s => s.Length > 0));
    public string Last => Ago.Length == 0 ? "no commits yet" : $"{Ago} · {Subject}";
}

public static class Git
{
    /// Repos in the CLI quick folders, up to two levels down (F:\Coding\group\repo). Newest commit first.
    public static async Task<List<Repo>> Repos()
    {
        var repos = await Task.WhenAll(Folders().Select(f => Read(f.Dir, f.Name)));
        return repos.OfType<Repo>().OrderByDescending(r => r.When).ToList();
    }

    /// Just where the repos are: a folder scan, no git calls.
    public static List<(string Dir, string Name)> Folders() => App.Settings.CliFolders.Where(Directory.Exists)
        .SelectMany(root => Find(root, 2).Select(dir => (dir, NameOf(root, dir))))
        .DistinctBy(f => f.dir, StringComparer.OrdinalIgnoreCase).ToList();

    // "dndreforged/dndhelper" under F:\Coding; a quick folder that is itself a repo shows its own name.
    static string NameOf(string root, string dir) =>
        (dir == root ? Path.GetFileName(root.TrimEnd('\\')) : Path.GetRelativePath(root, dir)).Replace('\\', '/');

    // Stops at a repo: its own subfolders are its business. Skips .hidden and node_modules.
    static IEnumerable<string> Find(string dir, int depth)
    {
        if (IsRepo(dir)) return [dir];
        if (depth == 0) return [];
        try
        {
            return Directory.GetDirectories(dir)
                .Where(d => Path.GetFileName(d) is var n && !n.StartsWith('.') && n != "node_modules")
                .SelectMany(d => Find(d, depth - 1)).ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return []; }
    }

    // A file, not a folder, for worktrees and submodules.
    static bool IsRepo(string dir) => Path.Combine(dir, ".git") is var g && (Directory.Exists(g) || File.Exists(g));

    static async Task<Repo?> Read(string dir, string name)
    {
        try
        {
            var status = Shell.Run("git", "-C", dir, "status", "--porcelain=v2", "--branch");
            var log = Shell.Run("git", "-C", dir, "log", "-1", "--format=%ct%x1f%cr%x1f%s");
            var remote = Shell.Run("git", "-C", dir, "remote", "get-url", "origin");
            var s = await status;
            if (s.Code != 0) { Db.Warn($"git status {dir}: {s.Err.Trim()}"); return null; }
            var (branch, ahead, behind, tracked, changes) = ParseStatus(s.Out);
            var l = (await log).Out.Trim().Split('\x1f');
            var r = await remote;
            return new Repo(name, dir, branch, ahead, behind, tracked, changes,
                l.Length == 3 ? long.Parse(l[0]) : 0, l.Length == 3 ? l[1] : "", l.Length == 3 ? l[2] : "",
                r.Code == 0 ? WebUrl(r.Out.Trim()) : null);
        }
        catch (Exception e) { Db.Warn($"git {dir}: {e.Message}"); return null; }
    }

    public static (string Branch, int Ahead, int Behind, bool Tracked, int Changes) ParseStatus(string porcelain)
    {
        string branch = "";
        int ahead = 0, behind = 0, changes = 0;
        var tracked = false;
        foreach (var line in porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("# branch.head ")) branch = line[14..].Trim();
            else if (line.StartsWith("# branch.upstream ")) tracked = true;
            else if (line.StartsWith("# branch.ab ") && line[12..].Split(' ') is [var a, var b, ..])
            {
                ahead = int.Parse(a.TrimStart('+'));
                behind = int.Parse(b.TrimStart('-'));
            }
            else if (!line.StartsWith('#')) changes++;
        }
        return (branch, ahead, behind, tracked, changes);
    }

    /// Browser URL for a remote: git@host:user/repo.git and https://user:token@host/user/repo.git both → https://host/user/repo.
    public static string? WebUrl(string remote)
    {
        if (remote.StartsWith("git@") && remote.IndexOf(':') is var colon and > 4)
            remote = $"https://{remote[4..colon]}/{remote[(colon + 1)..]}";
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")) return null;
        var path = u.AbsolutePath.EndsWith(".git") ? u.AbsolutePath[..^4] : u.AbsolutePath;
        return $"https://{u.Authority}{path}"; // Authority leaves out user:token
    }
}
