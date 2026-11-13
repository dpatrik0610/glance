using System.IO;
using System.Net.Http;
using System.Windows;
using Glance.Services;

namespace Glance;

/// `Glance.exe --selftest`: exit code 0 = pass. Covers the logic that's easy to get subtly wrong.
static class SelfTest
{
    public static int Run()
    {
        try
        {
            for (var i = 0; i < 500; i++)
            {
                var (p, bits) = Passwords.Generate(12, true, true, true, true, true);
                Check(p.Length == 12, "length");
                Check(p.Any(char.IsUpper) && p.Any(char.IsLower) && p.Any(char.IsDigit) && p.Any(c => !char.IsLetterOrDigit(c)), $"classes: {p}");
                Check(!p.Any("Il1O0o".Contains), $"look-alikes: {p}");
                Check(bits > 70, "entropy");
            }
            Check(Passwords.Generate(30, false, false, true, false, false).Password.All(char.IsDigit), "digits only");

            Check(Native.TryParseHotkey("Win+Shift+G", out var m, out var vk) && m == 12 && vk == 0x47, "Win+Shift+G");
            Check(Native.TryParseHotkey("ctrl + alt + 1", out m, out vk) && m == 3 && vk == 0x31, "ctrl+alt+1");
            Check(!Native.TryParseHotkey("G", out _, out _), "needs modifier");
            Check(!Native.TryParseHotkey("Ctrl+Nope", out _, out _), "bad key");

            Check(Native.Port(0x5000) == 80 && Native.Port(0x901F) == 8080, "port byte order");
            Check(Native.ListeningPorts().Any(p => p.Port == 135 && p.Process == "svchost"), "tcp table: RPC on 135"); // always listening on Windows
            var procs = Native.Processes();
            var me = System.Diagnostics.Process.GetCurrentProcess();
            Check(procs.Count > 20 && procs.Count(p => p.Pid == Environment.ProcessId) == 1, "process list: has this process once");
            var self = procs.First(p => p.Pid == Environment.ProcessId);
            Check(self.Name == me.ProcessName && self.Cpu > TimeSpan.Zero && self.Cpu <= me.TotalProcessorTime + TimeSpan.FromSeconds(1)
                && Math.Abs(self.WorkingSet - me.WorkingSet64) < 64 << 20, $"process list: {self} vs {me.ProcessName} {me.TotalProcessorTime} {me.WorkingSet64}");
            Check(procs.Any(p => p.Pid == 4 && p.Name == "System") && procs.Any(p => p.Name == "svchost"), "process list: names without .exe");
            Check(procs.Sum(p => p.Io) > 0, "process list: I/O counters");
            Native.GpuByPid(); // the first sample only sets the baseline
            Thread.Sleep(300);
            var gpu = Native.GpuByPid();
            Check(gpu.Count > 0 && gpu.Values.All(v => v is >= 0 and <= 100), $"gpu per process: {gpu.Count} pids"); // dwm always has engines
            Check(Native.Connections().All(c => c.Port is > 0 and < 65536 && !c.Remote.StartsWith("127.")), "tcp table: connections");

            Check(SpeedGauge.Fraction(0) == 0 && SpeedGauge.Fraction(-5) == 0 && SpeedGauge.Fraction(2500) == 1 && SpeedGauge.Fraction(9000) == 1, "dial: ends");
            Check(Math.Abs(SpeedGauge.Fraction(100) - 0.5) < 1e-9 && Math.Abs(SpeedGauge.Fraction(75) - 0.4375) < 1e-9, "dial: steps");

            var now = new DateTime(2026, 10, 31, 20, 0, 0); // a Saturday evening
            DateTime? D(string s) => Due.Parse(s, now);
            Check(D("tomorrow 9") == new DateTime(2026, 11, 1, 9, 0, 0), "tomorrow 9");
            Check(D("in 30m") == now.AddMinutes(30) && D("in 2h") == now.AddHours(2), "relative");
            Check(D("18:00") == new DateTime(2026, 11, 1, 18, 0, 0), "past time-only → tomorrow");
            Check(D("21:30") == new DateTime(2026, 10, 31, 21, 30, 0), "future time-only → today");
            Check(D("fri 10") == new DateTime(2026, 11, 6, 10, 0, 0), "weekday");
            Check(D("sat 9") == new DateTime(2026, 11, 7, 9, 0, 0), "same weekday, passed → next week");
            Check(D("11.03") == new DateTime(2026, 11, 3, 9, 0, 0), "hu month.day, default 9:00");
            Check(D("01.15") == new DateTime(2027, 1, 15, 9, 0, 0), "past date → next year");
            Check(D("2026-12-24 18:30") == new DateTime(2026, 12, 24, 18, 30, 0), "iso");
            Check(D("") is null && D("nonsense") is null && D("25:00") is null && D("02.30") is null, "rejects bad input");
            foreach (var d in new[] { now.AddHours(1), now.AddDays(1), now.AddDays(3), now.AddDays(40), now.AddDays(400), now.AddHours(-2) })
            {
                var rounded = d.AddSeconds(-d.Second);
                Check(D(Due.Format(rounded, now)) == rounded, $"round-trip {Due.Format(rounded, now)}");
            }

            var px = new byte[] { 1, 2, 3, 255, 4, 5, 6, 128, 7, 8, 9, 0, 10, 11, 12, 255 };
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, 8);
            var back = new byte[16];
            System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(ClipboardHistory.Encode(bmp)),
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad)
                .Frames[0].CopyPixels(back, 8, 0);
            Check(back.SequenceEqual(px), "clipboard image: png keeps pixels and alpha");

            // Stickies: everything a sticky holds survives a save and load (an absolute name keeps it out of AppData)
            var tmp = Path.Combine(Path.GetTempPath(), $"glance-selftest-{Environment.ProcessId}.json");
            try
            {
                var store = new JsonStore<Sticky>(tmp);
                Check(store.Load(), "store: no file yet");
                var sticky = new Sticky { Title = "t", Kind = NoteKind.Checklist, Color = "Blue", Opacity = 0.5, Left = -10, Width = 300, Shown = false };
                sticky.Items.Add(new TodoItem { Text = "a", Done = true, Due = new DateTime(2026, 1, 2, 3, 4, 0), Remind = true, Reminded = true });
                store.Items.Add(sticky);
                store.Items.Add(new Sticky()); // never placed: no position yet
                store.Flush();
                var reread = new JsonStore<Sticky>(tmp);
                Check(!reread.Load() && reread.Items.Count == 2, "store: reload");
                Check(reread.Items[0] is { Title: "t", Kind: NoteKind.Checklist, Color: "Blue", Opacity: 0.5, Left: -10, Top: null, Width: 300, Shown: false }
                      && reread.Items[0].Items[0] is { Text: "a", Done: true, Remind: true, Reminded: true } && reread.Items[1].Left is null,
                    "store: sticky round-trip");
            }
            finally { File.Delete(tmp); }

            // Calendar: which days an event shows on (overnight, all-day with Google's exclusive end, zero-length)
            var cal = new CalInfo("c", "c", System.Windows.Media.Colors.Red, true, true, []);
            CalEvent Ev(DateTime s, DateTime e, bool allDay) => new("e", cal, "t", s, e, allDay, "", "", [], true, false);
            DateTime d1 = new(2026, 3, 10), d2 = d1.AddDays(1), d3 = d1.AddDays(2);
            var night = Ev(d1.AddHours(23), d2.AddHours(1), false);
            var allDay2 = Ev(d1, d3, true); // the 10th and 11th
            var point = Ev(d2, d2, false);
            Check(night.On(d1) && night.On(d2) && !night.On(d3), "calendar: overnight event");
            Check(allDay2.On(d1) && allDay2.On(d2) && !allDay2.On(d3) && !allDay2.On(d1.AddDays(-1)), "calendar: all-day end is exclusive");
            Check(point.On(d2) && !point.On(d1), "calendar: zero-length event");

            // Images: crop, resize, every format, EXIF rotation
            var grad = new byte[200 * 100 * 4];
            for (var i = 0; i < grad.Length; i += 4) (grad[i], grad[i + 1], grad[i + 2], grad[i + 3]) = ((byte)(i / 4 % 200), (byte)(i / 800), 128, 255);
            var photo = System.Windows.Media.Imaging.BitmapSource.Create(200, 100, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, grad, 800);
            var cut = ImageOps.Resize(ImageOps.Crop(photo, new System.Windows.Int32Rect(10, 10, 100, 50)), 50, 25);
            Check(cut.PixelWidth == 50 && cut.PixelHeight == 25, "image crop + resize");
            Check(ImageOps.Encode(photo, ImageOps.Formats[0], 30).Length < ImageOps.Encode(photo, ImageOps.Formats[0], 90).Length, "jpg quality");
            foreach (var f in ImageOps.Formats)
            {
                var again = ImageOps.Load(new MemoryStream(ImageOps.Encode(cut, f, 80)));
                Check(again.PixelWidth == 50 && again.PixelHeight == 25, $"image {f.Name} round-trip");
            }
            var exif = new System.Windows.Media.Imaging.BitmapMetadata("jpg");
            exif.SetQuery("/app1/ifd/{ushort=274}", (ushort)6); // shot with the phone upright: stored sideways
            var jpeg = new System.Windows.Media.Imaging.JpegBitmapEncoder();
            jpeg.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(photo, null, exif, null));
            var sideways = new MemoryStream();
            jpeg.Save(sideways);
            sideways.Position = 0;
            var upright = ImageOps.Load(sideways);
            Check(upright.PixelWidth == 100 && upright.PixelHeight == 200, $"exif orientation: {upright.PixelWidth}×{upright.PixelHeight}");

            // Color math
            var rnd = new Random(7);
            for (var i = 0; i < 300; i++)
            {
                var c = System.Windows.Media.Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
                var (h, s, v) = ColorMath.ToHsv(c);
                var (hh, ss, ll) = ColorMath.ToHsl(c);
                Check(ColorMath.FromHsv(h, s, v) == c && ColorMath.FromHsl(hh, ss, ll) == c, $"color round-trip {ColorMath.Hex(c)}");
                Check(ColorMath.TryParse(ColorMath.Hex(c), out var back2) && back2 == c, "hex round-trip");
            }
            Check(ColorMath.TryParse("#f60", out var f60) && f60 == System.Windows.Media.Color.FromRgb(255, 102, 0)
                && ColorMath.TryParse("rgb(1, 2, 3)", out var rgb) && rgb.B == 3 && !ColorMath.TryParse("#12345", out _)
                && !ColorMath.TryParse("300 0 0", out _), "color parse");
            Check(Math.Abs(ColorMath.Contrast(System.Windows.Media.Colors.Black, System.Windows.Media.Colors.White) - 21) < 1e-9
                && ColorMath.Grade(4.5) == "AA" && ColorMath.Grade(2.9) == "Fail", "wcag contrast");
            var (ol, oc, oh) = ColorMath.ToOklch(System.Windows.Media.Color.FromRgb(255, 0, 0));
            Check(Math.Abs(ol - 0.628) < 0.001 && Math.Abs(oc - 0.2577) < 0.001 && Math.Abs(oh - 29.23) < 0.05, $"oklch red: {ol} {oc} {oh}");
            var tri = ColorMath.Palettes(System.Windows.Media.Color.FromRgb(255, 0, 0)).First(p => p.Name == "Triadic").Colors;
            Check(tri.Select(ColorMath.Hex).SequenceEqual(["#FF0000", "#00FF00", "#0000FF"]), "triadic: 120° apart");

            Check(App.CliArg(["--cli", "C:\""]) == @"C:\" && App.CliArg(["--cli", @"F:\Coding"]) == @"F:\Coding", "explorer drive-root quirk");
            Check(App.CliArg(["--tray"]) is null, "no --cli");

            var st = Git.ParseStatus("# branch.oid abc\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +2 -1\n1 .M N... a\n? new.txt\n");
            Check(st == ("main", 2, 1, true, 2), "git porcelain v2");
            Check(Git.ParseStatus("# branch.head dev\n") == ("dev", 0, 0, false, 0), "git: no upstream, clean");
            Check(Git.WebUrl("git@github.com:me/glance.git") == "https://github.com/me/glance", "git ssh remote");
            Check(Git.WebUrl("https://me:tok@github.com/me/glance.git") == "https://github.com/me/glance", "git https remote drops token");
            Check(Git.WebUrl("F:/repos/x") is null, "git local remote");
            Check(Shell.OnPath("cmd.exe") && !Shell.OnPath("no-such-tool.exe"), "tool detection hides Git/Docker tabs");

            var mark = Pages.CliPage.CwdMark().Match("x\x1b]9;9;\"F:\\Coding\\glance\"\x1b\\user");
            Check(mark.Success && mark.Groups[1].Value == @"F:\Coding\glance", "cli cwd mark");

            Task<string?> Nop() => Task.FromResult<string?>(null);
            Cmd[] cmds = [new("Network", "Page", "", Nop), new("Copy public IP", "Network", "", Nop), new("glance", "Repo", "", Nop, @"F:\Coding\glance"),
                new("todo", "Note", "", Nop, "buy milk", true)];
            // Opening hours. 2026-11-02 is a Monday.
            string? H(string spec, int day, int h, int min = 0) => OpeningHours.Status(spec, new DateTime(2026, 11, 1 + day, h, min, 0), out _);
            const string shop = "Mo-Fr 08:00-18:00; Sa 09:00-13:00; PH off";
            Check(H(shop, 1, 10) == "Open · closes 18:00", "hours: weekday open");
            Check(H(shop, 1, 7) == "Closed · opens 08:00", "hours: before opening");
            Check(H(shop, 5, 19) == "Closed · opens tomorrow 09:00", "hours: friday evening");
            Check(H(shop, 6, 14) == "Closed · opens Mon 08:00", "hours: saturday after close");
            Check(H("Mo-Su 18:00-02:00", 2, 1) == "Open · closes 02:00", "hours: past midnight");
            Check(H("Mo-Fr 09:00-17:00, Sa 10:00-12:00", 6, 11) == "Open · closes 12:00", "hours: comma-separated rules");
            Check(H("Mo-Fr 11:00-14:30,17:00-22:00", 3, 15) == "Closed · opens 17:00", "hours: lunch break");
            Check(H("24/7", 0, 3) == "Open 24/7" && H("sunrise-sunset", 1, 10) is null && H("", 1, 10) is null, "hours: 24/7, unknown syntax");
            Check(Places.Pattern("Pizza") == "p[ií]zz" && Places.Pattern("kfc") == "kfc", "search pattern: stem, accents");
            Check(Places.Pattern("c++ (x)") == @"c\+\+ \(x\)", "search pattern: regex chars literal");
            if (System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                Check(Places.Why(new HttpRequestException("", null, System.Net.HttpStatusCode.BadGateway)).Contains("their server")
                    && Places.Why(new TaskCanceledException()).Contains("their server")
                    && Places.Why(new HttpRequestException("dns")).Contains("connection"), "map errors name the service");
            Check(Places.ShortName("12, Andrássy út, Terézváros, Budapest") == "12, Andrássy út", "place short name");

            static int Peak(byte[] w) => Enumerable.Range(0, (w.Length - 44) / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(w, 44 + i * 2)));
            foreach (var name in Chime.Names)
            {
                var wav = Chime.Wave(name, 1);
                Check(wav.Length > 80_000 && wav[0] == (byte)'R' && BitConverter.ToInt32(wav, 4) == wav.Length - 8, $"{name} wav header");
                Check(Peak(wav) is > 5000 and < 32767, $"{name} level: {Peak(wav)}"); // audible, never clipping
            }
            Check(Math.Abs(Peak(Chime.Wave("Chime", 0.5)) * 2 - Peak(Chime.Wave("Chime", 1))) < 4, "chime volume scales");

            foreach (var (aumid, name) in new[] { ("Spotify.exe", "Spotify"), ("Chrome", "Chrome"), ("308046B0AF4A39CB", "Firefox"),
                         ("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Media Player"), ("Acme.Tunes_1x2y!App", "Tunes"), ("foobar2000.exe", "foobar2000") })
                Check(Player.SourceName(aumid) == name, $"player name {aumid}: {Player.SourceName(aumid)}");

            Check(MainWindow.Match(cmds, "").Count == 3, "palette: notes only on search");
            Check(MainWindow.Match(cmds, "net").Select(c => c.Title).SequenceEqual(["Network", "Copy public IP"]), "palette: title prefix first");
            Check(MainWindow.Match(cmds, "coding gla").Single().Title == "glance" && MainWindow.Match(cmds, "milk").Single().Title == "todo", "palette: all words, extra text");

            var cfg = new Settings { GpuTempLimit = 85, DiskFreeLimit = 0 };
            var alerts = new Alerts();
            var fired = 0;
            alerts.Fired += _ => fired++;
            var t = DateTime.UtcNow;
            void Gpu(float temp, int times, TimeSpan at) { for (var i = 0; i < times; i++) alerts.Check(Snap(temp), cfg, t + at); }
            Gpu(90, 2, TimeSpan.Zero);
            Check(fired == 0, "debounce");
            Gpu(90, 1, TimeSpan.Zero);
            Check(fired == 1, "fires after streak");
            Gpu(90, 5, TimeSpan.Zero);
            Gpu(82, 1, TimeSpan.Zero); // inside hysteresis: stays armed-off
            Gpu(90, 3, TimeSpan.Zero);
            Check(fired == 1, "no repeat while active");
            Gpu(70, 1, TimeSpan.Zero);
            Gpu(90, 3, TimeSpan.FromMinutes(1));
            Check(fired == 1, "cooldown");
            Gpu(70, 1, TimeSpan.FromMinutes(2));
            Gpu(90, 3, TimeSpan.FromMinutes(11));
            Check(fired == 2, "re-fires after cooldown");

            CompareChecks();

            static string Rel(string tag, string asset, string? digest) =>
                $$"""{"tag_name":"{{tag}}","assets":[{"name":"{{asset}}","browser_download_url":"https://x/{{asset}}"{{(digest is null ? "" : $",\"digest\":\"{digest}\"")}}}]}""";
            var v110 = new Version(1, 1, 0);
            var up = Updater.Newer(Rel("v1.1.1", "Glance-1.1.1-win-x64.zip", "sha256:ab12"), v110);
            Check(up is { Sha256: "ab12", Url: "https://x/Glance-1.1.1-win-x64.zip" } && up.Version == new Version(1, 1, 1), "update: newer patch offered");
            Check(Updater.Newer(Rel("v1.1.0", "Glance-1.1.0-win-x64.zip", "sha256:ab12"), new Version(1, 1, 0, 0)) is null, "update: same version (4-part) not offered");
            Check(Updater.Newer(Rel("v1.0.9", "Glance-1.0.9-win-x64.zip", "sha256:ab12"), v110) is null, "update: older not offered");
            Check(Updater.Newer(Rel("v1.2.0", "Glance-1.2.0-win-x64.zip", null), v110) is null, "update: no digest, not offered");
            Check(Updater.Newer(Rel("v1.2.0", "notes.txt", "sha256:ab12"), v110) is null, "update: no zip, not offered");

            // JSON page: strict check, error position as an editor counts it, formatting that keeps values as written
            Check(JsonText.Check("""{"a":[1,2],"b":"é"}""") is { Ok: true, Message: "object · 2 keys" }, "json: valid object");
            Check(JsonText.Check("  ") is { Ok: false, Message: "" }, "json: blank is neither valid nor an error");
            var bad = JsonText.Check("{\r\n  \"a\": 1,\r\n  \"ő\" 2\r\n}"); // ő: 2 bytes, 1 column
            Check(bad is { Ok: false, ErrorAt: 20 } && bad.Message.StartsWith("Line 3, column 7: "), $"json: error at line 3 col 7 ({bad.Message}, at {bad.ErrorAt})");
            Check(!JsonText.Check("""{"a":1,}""").Ok && !JsonText.Check("// x\n1").Ok, "json: no trailing commas or comments");
            const string raw = """{"n":1.50e3,"s":"é😀","t":"\\uD83D\\uDE00","a":[]}"""; // t: a backslash, then text that only looks like an escape
            Check(JsonText.Format(raw, false) == raw, $"json: minify keeps numbers and text as written ({JsonText.Format(raw, false)})");
            var jp = new Pages.JsonPage();
            jp.Box.Text = "[1,";
            Check(!jp.FormatButton.IsEnabled && jp.CopyButton.IsEnabled && jp.StatusText.Text.StartsWith("Line 1"), "json page: error shown, format off");
            jp.Box.Text = "[1,2]";
            Check(jp.FormatButton.IsEnabled && jp.StatusText.Text == "Valid JSON · array · 2 items", "json page: valid");
            jp.Box.Text = "[1,\n2,\n]";
            jp.Measure(new System.Windows.Size(600, 400));
            jp.Arrange(new System.Windows.Rect(0, 0, 600, 400));
            jp.UpdateLayout();
            jp.Place();
            var gutter = new System.Windows.Documents.TextRange(jp.Gutter.ContentStart, jp.Gutter.ContentEnd).Text.ReplaceLineEndings("\n");
            Check(gutter == "1\n2\n3" && jp.ErrorLine.Visibility == System.Windows.Visibility.Visible && System.Windows.Controls.Canvas.GetTop(jp.ErrorLine) > 0
                  && jp.GoButton.Visibility == System.Windows.Visibility.Visible, "json page: line numbers, error line marked");
            Check(JsonText.Format("""{"a":[1]}""", true).ReplaceLineEndings("\n") == "{\n  \"a\": [\n    1\n  ]\n}", "json: format indents by 2");

            Console.Error.WriteLine($"selftest: all {checks} checks passed");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    // Compare page: byte compare, diff parsing, folder compare, duplicates, and the guard that keeps recycling from taking a last copy
    static void CompareChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"glance-selftest-cmp-{Environment.ProcessId}");
        string Put(string rel, byte[] bytes)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bytes);
            return p;
        }
        string W(string rel, string text) => Put(rel, System.Text.Encoding.UTF8.GetBytes(text));
        try
        {
            var big = new byte[200_000];
            new Random(1).NextBytes(big);
            var x = Put("x.bin", big);
            Check(Compare.FirstDifference(x, Put("x2.bin", big)) == -1, "compare: identical across 64 KB blocks");
            var y = (byte[])big.Clone();
            y[70_000] ^= 1;
            Check(Compare.FirstDifference(x, Put("y.bin", y)) == 70_000, "compare: first difference in the second block");
            Check(Compare.FirstDifference(x, Put("short.bin", big[..65_536])) == 65_536, "compare: shorter file differs where it ends");
            Check(Compare.FirstDifference(Put("e1", []), Put("e2", [])) == -1, "compare: two empty files");
            Check(!Compare.IsText(x) && Compare.IsText(W("t.txt", "hello")), "compare: binary vs text");

            var d = Compare.ParseUnified(string.Join('\n', "diff --git a/a b/b", "index 1..2 100644", "--- a/a.txt", "+++ b/b.txt",
                "@@ -1,7 +1,6 @@", " one", "-two", "-three x", "+three y", " four", "-gone line", "-also gone", "+new line", " five", "+six",
                "@@ -40,2 +39,2 @@", "-x", @"\ No newline at end of file", "+y", ""));
            var kinds = new string(d.Select(l => l.Kind).ToArray());
            Check(kinds == " --+ --+ +@-+", "diff: line kinds " + kinds);
            Check(d[2] is { Left: 3, Right: null } && d[3] is { Left: null, Right: 2 } && d[9] is { Left: null, Right: 6 }, "diff: line numbers");
            Check(d[11] is { Left: 40, Right: null } && d[12] is { Left: null, Right: 39 }, "diff: second hunk numbering");
            Check(d[1].Segs is [{ Kind: '-', Text: "two" }], "diff: unlike lines stay unpaired (two vs three y)");
            Check(d[2].Segs is [{ Kind: ' ', Text: "three " }, { Kind: '-', Text: "x" }] && d[3].Segs is [{ Kind: ' ' }, { Kind: '+', Text: "y" }],
                "diff: an edited line marks only what changed");
            Check(d[5].Segs is [{ Kind: '-', Text: "gone" }, { Kind: ' ', Text: " line" }] && d[6].Segs is [{ Kind: '-', Text: "also gone" }],
                "diff: pairs the similar removed line, not just the first");
            if (Shell.OnPath("git.exe"))
            {
                // Off the UI thread: blocking on it here would deadlock with the await inside Shell.Run.
                List<DiffLine> Diff(string a, string b) => Task.Run(() => Compare.Diff(a, b)).Result;
                var real = Diff(W("a.txt", "one\r\ntwo\r\nthree\r\n"), W("b.txt", "one\ntwo!\nthree\n"));
                Check(real.Count(l => l.Kind == '-') == 1 && real.Single(l => l.Kind == '+') is { Right: 2, Segs: [_, { Text: "!" }] },
                    "diff: real git run, CRLF vs LF ignored");
                Check(Diff(W("c.txt", "a\r\nb\r\n"), W("d.txt", "a\nb\n")).Count == 0, "diff: only line endings differ → no lines");
            }

            W(@"L\same.txt", "same"); W(@"R\same.txt", "same");
            W(@"L\sub\edit.txt", "aaaa"); W(@"R\SUB\edit.txt", "aaab"); // same size; paths match ignoring case
            W(@"L\size.txt", "a"); W(@"R\size.txt", "ab");
            W(@"L\gone.txt", "x"); W(@"R\new.txt", "y");
            File.SetAttributes(W(@"R\hidden.txt", "h"), FileAttributes.Hidden);
            var (items, same) = Compare.Folders(Path.Combine(root, "L"), Path.Combine(root, "R"), default);
            var got = string.Join(",", items.Select(i => $"{i.Kind}{i.Path}"));
            Check(same == 1 && got == @"<gone.txt,>new.txt,≠size.txt,≠sub\edit.txt", "folders: " + got);

            W(@"D\a\one.txt", "dup content");
            File.SetLastWriteTime(W(@"D\b\two.txt", "dup content"), DateTime.Now.AddDays(1));
            W(@"D\b\other.txt", "dup contenu"); // same size, other bytes
            W(@"D\e1.txt", ""); W(@"D\e2.txt", "");
            var dups = Compare.Duplicates([Path.Combine(root, "D"), Path.Combine(root, @"D\a")], null, default); // overlapping roots
            Check(dups is [{ Files.Count: 2, Wasted: 11 }] && dups[0].Files[0].Path.EndsWith("one.txt"),
                "duplicates: one set, oldest first, no file listed twice, empty files left out");
            var (keep, copy) = (dups[0].Files[0].Path, dups[0].Files[1].Path);
            Check(!Compare.StillDuplicate(keep, keep), "recycle guard: a file is never its own copy");
            Check(Compare.StillDuplicate(copy, keep), "recycle guard: a true copy");
            File.WriteAllText(copy, "dup CONTENT");
            Check(!Compare.StillDuplicate(copy, keep), "recycle guard: edited since the scan");
            File.Delete(keep);
            Check(!Compare.StillDuplicate(copy, keep), "recycle guard: the copy that stays is gone");
            Check(Compare.Size(1536) == "2 KB" && Compare.Size(5L << 30) == "5.0 GB", "size text");

            // The page and every row template build and lay out (a bad resource name only shows up at runtime)
            var page = new Pages.ComparePage();
            page.DiffList.ItemsSource = d;
            page.FolderList.ItemsSource = items;
            page.DupeList.ItemsSource = new Pages.ComparePage.DupRow[] { new(dups[0], null), new(dups[0], dups[0].Files[1]) };
            foreach (var view in new UIElement[] { page.FilesView, page.FoldersView, page.DupesView }) view.Visibility = Visibility.Visible;
            page.Measure(new Size(1000, 700));
            page.Arrange(new Rect(0, 0, 1000, 700));
            page.UpdateLayout();
            Check(page.DiffList.ItemContainerGenerator.ContainerFromIndex(2) is not null, "compare page: diff rows built");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static Snapshot Snap(float gpuTemp) => new("", 0, null, null, null, "", 0, gpuTemp, null, null, null, null, null, null, 0, 0, [], []);

    static int checks;

    static void Check(bool ok, string what)
    {
        checks++;
        if (!ok) throw new Exception("selftest failed: " + what);
    }
}
