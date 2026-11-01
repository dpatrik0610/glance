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
            Check(Native.Connections().All(c => c.Port is > 0 and < 65536 && !c.Remote.StartsWith("127.")), "tcp table: connections");

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

            Check(App.CliArg(["--cli", "C:\""]) == @"C:\" && App.CliArg(["--cli", @"F:\Coding"]) == @"F:\Coding", "explorer drive-root quirk");
            Check(App.CliArg(["--tray"]) is null, "no --cli");

            var st = Git.ParseStatus("# branch.oid abc\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +2 -1\n1 .M N... a\n? new.txt\n");
            Check(st == ("main", 2, 1, true, 2), "git porcelain v2");
            Check(Git.ParseStatus("# branch.head dev\n") == ("dev", 0, 0, false, 0), "git: no upstream, clean");
            Check(Git.WebUrl("git@github.com:me/glance.git") == "https://github.com/me/glance", "git ssh remote");
            Check(Git.WebUrl("https://me:tok@github.com/me/glance.git") == "https://github.com/me/glance", "git https remote drops token");
            Check(Git.WebUrl("F:/repos/x") is null, "git local remote");
            Check(Shell.OnPath("cmd.exe") && !Shell.OnPath("no-such-tool.exe"), "tool detection hides Git/Docker tabs");

            var mark = Pages.CliPage.CwdMark().Match("x\x1b]9;9;\"F:\\Coding\\glance\"\x1b\\qantanas");
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
            Check(Places.ShortName("12, Andrássy út, Terézváros, Budapest") == "12, Andrássy út", "place short name");

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
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    static Snapshot Snap(float gpuTemp) => new("", 0, null, null, null, "", 0, gpuTemp, null, null, null, null, null, null, 0, 0, [], []);

    static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception("selftest failed: " + what);
    }
}
