using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glance.Services;

/// Updates from the public releases repo (the source repo is private). On the user's OK: download, check the SHA-256
/// GitHub lists for the zip, swap the files once Glance has exited, start it again. Settings live in %APPDATA%, untouched.
public static class Updater
{
    public record Release(Version Version, string Url, string Sha256);

    const string Latest = "https://api.github.com/repos/dpatrik0610/glance-releases/releases/latest";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static Updater() => http.DefaultRequestHeaders.UserAgent.ParseAdd("Glance"); // GitHub's API refuses requests without one

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version!);

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// The latest release when it's newer than this build, else null.
    public static async Task<Release?> Check() => Newer(await http.GetStringAsync(Latest), Current);

    internal static Release? Newer(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v'), out var v) || Normalize(v) <= current) return null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
            if (a.GetProperty("name").GetString()?.EndsWith("-win-x64.zip") == true
                && a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:"))
                return new(Normalize(v), a.GetProperty("browser_download_url").GetString()!, digest[7..]);
        return null; // no verifiable zip: not offered
    }

    /// Downloads and unpacks the release, then starts a hidden script that waits for this process to exit,
    /// copies the new files over the app folder and starts Glance again. The caller quits Glance right after.
    public static async Task Install(Release r)
    {
        var dir = Path.Combine(Path.GetTempPath(), "glance-update");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "update.zip");
        await using (var f = File.Create(zip))
        await using (var s = await http.GetStreamAsync(r.Url))
            await s.CopyToAsync(f);
        string hash;
        await using (var f = File.OpenRead(zip)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(f));
        if (hash != r.Sha256) throw new InvalidDataException("The download doesn't match the SHA-256 GitHub lists for it.");
        var files = Path.Combine(dir, "files");
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, files));
        if (!File.Exists(Path.Combine(files, "Glance.exe"))) throw new InvalidDataException("The zip has no Glance.exe.");

        // ponytail: a copy that fails (e.g. Glance in Program Files, not writable) just restarts the old version.
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var app = AppContext.BaseDirectory.TrimEnd('\\');
        var script = $$"""
            Wait-Process -Id {{Environment.ProcessId}} -Timeout 30 -ErrorAction Ignore
            robocopy {{Q(files)}} {{Q(app)}} /E /R:10 /W:1 /NJH /NJS /NDL /NFL /NP | Out-Null
            if ($LASTEXITCODE -lt 8) { Remove-Item {{Q(dir)}} -Recurse -Force -ErrorAction Ignore }
            explorer.exe {{Q(Path.Combine(app, "Glance.exe"))}}
            """;
        Process.Start(new ProcessStartInfo(Shell.PowerShell,
            $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}")
            { CreateNoWindow = true, UseShellExecute = false });
    }
}
