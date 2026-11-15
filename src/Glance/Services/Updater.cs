using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glance.Services;

/// Updates from this repo's GitHub releases. On the user's OK: download, check the SHA-256
/// GitHub lists for it, swap the files once Glance has exited, start it again. Settings live in %APPDATA%, untouched.
/// A copy installed by the setup updates by running the new setup silently (so Settings > Apps shows the right
/// version); a portable copy from the zip gets the zip copied over it.
public static class Updater
{
    public record Release(Version Version, string Url, string Sha256)
    {
        public bool Setup => Url.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// Inno Setup leaves its uninstaller next to the exe it installed (installer\Glance.iss).
    public static bool Installed { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    const string Latest = "https://api.github.com/repos/dpatrik0610/glance/releases/latest";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static Updater() => http.DefaultRequestHeaders.UserAgent.ParseAdd("Glance"); // GitHub's API refuses requests without one

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version!);

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// The latest release when it's newer than this build, else null.
    public static async Task<Release?> Check() => Newer(await http.GetStringAsync(Latest), Current, Installed);

    /// Installed copies take the setup when the release has one (older releases don't), else the zip.
    internal static Release? Newer(string json, Version current, bool installed = false)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v'), out var v) || Normalize(v) <= current) return null;
        Release? zip = null, setup = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (a.GetProperty("name").GetString() is not { } name
                || !a.TryGetProperty("digest", out var d) || d.GetString() is not { } digest || !digest.StartsWith("sha256:")) continue;
            var r = new Release(Normalize(v), a.GetProperty("browser_download_url").GetString()!, digest[7..]);
            if (name.EndsWith("-win-x64.zip")) zip = r;
            else if (name.EndsWith("-setup.exe")) setup = r;
        }
        return installed && setup is not null ? setup : zip; // null: nothing verifiable, not offered
    }

    /// Downloads the release, then starts a hidden script that waits for this process to exit, runs the setup
    /// (or copies the zip's files over the app folder) and starts Glance again. The caller quits Glance right after.
    public static async Task Install(Release r)
    {
        var dir = Path.Combine(Path.GetTempPath(), "glance-update");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, r.Setup ? "setup.exe" : "update.zip");
        await using (var f = File.Create(file))
        await using (var s = await http.GetStreamAsync(r.Url))
            await s.CopyToAsync(f);
        string hash;
        await using (var f = File.OpenRead(file)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(f));
        if (hash != r.Sha256) throw new InvalidDataException("The download doesn't match the SHA-256 GitHub lists for it.");

        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var app = AppContext.BaseDirectory.TrimEnd('\\');
        string swap;
        if (r.Setup) // per-user setup: no UAC; it installs into the folder it used before, which is this one
            swap = $$"""$ok = (Start-Process {{Q(file)}} '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru).ExitCode -eq 0""";
        else
        {
            var files = Path.Combine(dir, "files");
            await Task.Run(() => ZipFile.ExtractToDirectory(file, files));
            if (!File.Exists(Path.Combine(files, "Glance.exe"))) throw new InvalidDataException("The zip has no Glance.exe.");
            // ponytail: a copy that fails (e.g. Glance in Program Files, not writable) just restarts the old version.
            swap = $$"""robocopy {{Q(files)}} {{Q(app)}} /E /R:10 /W:1 /NJH /NJS /NDL /NFL /NP | Out-Null; $ok = $LASTEXITCODE -lt 8""";
        }
        var script = $$"""
            Wait-Process -Id {{Environment.ProcessId}} -Timeout 30 -ErrorAction Ignore
            {{swap}}
            if ($ok) { Remove-Item {{Q(dir)}} -Recurse -Force -ErrorAction Ignore }
            explorer.exe {{Q(Path.Combine(app, "Glance.exe"))}}
            """;
        Process.Start(new ProcessStartInfo(Shell.PowerShell,
            $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}")
            { CreateNoWindow = true, UseShellExecute = false });
    }
}
