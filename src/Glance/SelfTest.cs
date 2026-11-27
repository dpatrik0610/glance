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
            Check(Settings.Dir.StartsWith(Path.GetTempPath()), $"selftest data stays out of AppData: {Settings.Dir}");
            // First, before anything reads a resource: styles built after the swap take the Flamingo colors.
            Skin.Load("Flamingo");
            var sub = (Skin.Style("Label")).Setters.OfType<Setter>().First(x => x.Property == System.Windows.Controls.TextBlock.ForegroundProperty);
            Check(sub.Value is System.Windows.Media.SolidColorBrush { Color: { R: 0xC9, G: 0xA3, B: 0xC0 } }, "theme swap reaches styles");
            for (var i = 0; i < 500; i++)
            {
                var (p, bits) = Passwords.Generate(12, true, true, true, true, true);
                Check(p.Length == 12, "length");
                Check(p.Any(char.IsUpper) && p.Any(char.IsLower) && p.Any(char.IsDigit) && p.Any(c => !char.IsLetterOrDigit(c)), $"classes: {p}");
                Check(!p.Any("Il1O0o".Contains), $"look-alikes: {p}");
                Check(bits > 70, "entropy");
            }
            Check(Passwords.Generate(30, false, false, true, false, false).Password.All(char.IsDigit), "digits only");

            App.Settings.Currency = "HUF"; // a fresh settings file starts in the Windows region's currency
            Check(Money.Format(-4500).EndsWith(" Ft") && Money.Format(-4500)[0] == '−' && Money.Format(12000)[0] == '+'
                && Money.Format(-4500, plus: false)[0] == '4', $"money format: {Money.Format(-4500)} {Money.Format(12000)} {Money.Format(-4500, plus: false)}");
            var quote = Stocks.Parse("aapl", """
                {"chart":{"result":[{"meta":{"currency":"USD","symbol":"AAPL","shortName":"Apple Inc.","regularMarketPrice":110},
                "indicators":{"quote":[{"close":[90,null,100,110]}]}}],"error":null}}
                """);
            Check(quote is { Symbol: "AAPL", Name: "Apple Inc.", Currency: "USD", Price: 110 } && Math.Abs(quote.Change - 10) < 1e-9
                && quote.Closes.Length == 4 && float.IsNaN(quote.Closes[1]), $"stock quote: {quote}");
            Check(Stocks.Parse("x", """{"chart":{"result":null,"error":{"code":"Not Found"}}}""") is null, "stock quote: unknown symbol");

            var rent = new Bill("Rent", 1000, new DateTime(2027, 1, 31), Repeat.Monthly, "Housing", Day: 31);
            var feb = rent with { Due = rent.NextDue() };
            Check(feb.Due == new DateTime(2027, 2, 28) && feb.NextDue() == new DateTime(2027, 3, 31), $"bills: the 31st stays the 31st where it can: {feb.Due:d} {feb.NextDue():d}");
            Check((rent with { Repeat = Repeat.Yearly }).NextDue() == new DateTime(2028, 1, 31) && (rent with { Repeat = Repeat.Weekly }).NextDue() == new DateTime(2027, 2, 7)
                && (rent with { Repeat = Repeat.Quarterly }).NextDue() == new DateTime(2027, 4, 30), "bills: yearly, weekly, quarterly");
            Check((rent with { Repeat = Repeat.Yearly, Amount = 1200 }).PerMonth == 100 && (rent with { Repeat = Repeat.Once }).PerMonth == 0, "bills: per month");
            Check(rent.When(new DateTime(2027, 1, 30)) == "Tomorrow" && rent.When(new DateTime(2027, 2, 2)) == "2 days late" && rent.When(new DateTime(2027, 1, 31)) == "Today",
                "bills: when");
            Money.Bills.Add(rent);
            Check(Money.DueReminders(new DateTime(2027, 1, 30, 8, 0, 0)).Count == 0 && Money.DueReminders(new DateTime(2027, 1, 30, 9, 0, 0)) is [{ Name: "Rent" }],
                "bills: reminder from 9:00 the day before");
            Money.Replace(rent, rent with { Reminded = rent.Due });
            Check(Money.DueReminders(new DateTime(2027, 1, 31)).Count == 0, "bills: a reminder shows once per due date");
            var entries = Money.Entries.Count;
            Money.Pay(Money.Bills[^1]);
            Check(Money.Entries.Count == entries + 1 && Money.Entries[^1].Amount == -1000 && Money.Bills[^1] is { Reminded: null } paid && paid.Due == new DateTime(2027, 2, 28),
                "bills: paid books an expense and moves on");
            Check(Money.Bills[^1].PaidUp(new DateTime(2027, 2, 24)) && !Money.Bills[^1].PaidUp(new DateTime(2027, 2, 25)), "bills: ticked until 3 days before the next one");
            Check(new Pages.BillsPage().Content is not null, "bills page loads");
            Check(new Pages.TodayPage().Content is not null, "today page loads");
            Money.Unpay(Money.Bills[^1]);
            Check(Money.Entries.Count == entries && Money.Bills[^1] is { Paid: null } unpaid && unpaid.Due == rent.Due, "bills: unticking undoes paid");
            Money.Bills.Clear();

            // Currencies: formatting by the currency's own rules, and a bill in another one booked at the rate.
            Check(Money.Format(12.99, plus: false, currency: "EUR") == $"{12.99:N2} €" && Money.Format(15, plus: false, currency: "EUR") == "15 €"
                && Money.Format(-1200, currency: "USD") == $"−${1200:N0}" && Money.Format(4500.4, plus: false, currency: "HUF") == $"{4500:N0} Ft",
                $"currency format: {Money.Format(12.99, plus: false, currency: "EUR")} {Money.Format(-1200, currency: "USD")} {Money.Format(4500.4, plus: false, currency: "HUF")}");
            Rates.Use("2027-01-01", new() { ["HUF"] = 400, ["USD"] = 1.25 });
            Check(Rates.Convert(10, "EUR", "HUF") == 4000 && Math.Abs(Rates.Convert(10, "USD", "HUF")!.Value - 3200) < 1e-9 && Rates.Convert(1, "JPY", "HUF") is null,
                "rates: cross rates through EUR, none for an unknown one");
            var netflix = new Bill("Netflix", 12.99, DateTime.Today.AddDays(3), Repeat.Monthly, "Subscription", Currency: "EUR");
            Money.Bills.Add(netflix);
            Check(Money.Pay(netflix) && Money.Entries[^1] is { Amount: var paidHuf, Note: var paidNote } && Math.Abs(paidHuf + 5196) < 1e-9 && paidNote.Contains("€"),
                $"bills: another currency is booked at today's rate: {Money.Entries[^1]}");
            Check(!Money.Pay(Money.Bills[^1] with { Currency = "JPY" }), "bills: no rate, nothing booked");
            Money.Bills.Clear();
            Money.Entries.RemoveAt(Money.Entries.Count - 1);

            var watch = new Cloudflare.Watch();
            Deploy Cf(string key, string id, string status) => new(key, key, id, status, "", "", "", "", DateTime.UtcNow);
            Check(watch.Check([Cf("worker:a", "1", "success"), Cf("pages:p", "x", "success")]).Count == 0, "cloudflare: the first look only remembers");
            Check(watch.Check([Cf("worker:a", "1", "success"), Cf("pages:p", "x", "success")]).Count == 0, "cloudflare: nothing new, no alert");
            Check(watch.Check([Cf("worker:a", "2", "success")]) is [{ Id: "2" }], "cloudflare: a new Worker deployment alerts");
            Check(watch.Check([Cf("pages:p", "y", "active")]).Count == 0, "cloudflare: a build still running doesn't alert");
            Check(watch.Check([Cf("pages:p", "y", "failure")]) is [{ Failed: true }], "cloudflare: a failed build alerts once it ends");
            Check(watch.Check([Cf("pages:p", "y", "failure"), Cf("worker:new", "9", "success")]) is [{ Key: "worker:new" }],
                "cloudflare: the same build doesn't alert twice; a new Worker does");
            Check(new Pages.CloudflarePage().Content is not null, "cloudflare page loads");

            Check(new Pages.BoardPage().Content is not null, "board page loads");
            Check(Timers.ParseDuration("25") == 1500 && Timers.ParseDuration("1:30") == 90 && Timers.ParseDuration("1:00:00") == 3600
                && Timers.ParseDuration("0") is null && Timers.ParseDuration("abc") is null && Timers.ParseDuration("-5") is null, "timer lengths");
            Check(Timers.Clock(90) == "01:30" && Timers.Clock(3725) == "1:02:05" && Timers.Clock(0.2) == "00:01" && Timers.Clock(-3) == "00:00", "timer clock");
            {
                var colorMenu = new System.Windows.Controls.MenuItem { Header = "Color" };
                colorMenu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Red", IsChecked = true });
                colorMenu.ApplyTemplate();
                Check(colorMenu.Template.FindName("PART_Popup", colorMenu) is System.Windows.Controls.Primitives.Popup, "menus open submenus");
            }
            {
                var h = new Pages.BoardPage.History("a");
                h.Record("a"); // no change: no step
                h.Record("b");
                h.Record("c");
                var undo = (h.Step(undo: true), h.Step(undo: true), h.Step(undo: true));
                var redo = h.Step(undo: false);
                h.Record("x"); // a new change drops what could be redone
                Check(undo == ("b", "a", null) && redo == "b" && h.Now == "x" && !h.CanRedo && h.Step(true) == "b", "board undo/redo");
            }
            {
                var dir = Directory.CreateTempSubdirectory("glance-board-").FullName;
                try
                {
                    string pics = Path.Combine(dir, "pics"), copies = Path.Combine(dir, "copies"), file = Path.Combine(dir, "Trip" + BoardFile.Extension);
                    Directory.CreateDirectory(pics);
                    File.WriteAllBytes(Path.Combine(pics, "p1.png"), [1, 2, 3]);
                    var b = new Board { Name = "Trip", Layout = "kanban", Ink = "abc" };
                    BoardItem n = new() { Title = "Pack", Text = "- [ ] socks" }, pic = new() { Kind = "image", File = "p1.png" };
                    b.Items.AddRange([n, pic]);
                    b.Edges.Add(new() { From = n.Id, To = pic.Id, Label = "then" });
                    BoardFile.Export(b, file, pics);
                    var again = BoardFile.Import(file, copies);
                    var n2 = again.Items[0];
                    var pic2 = again.Items[1];
                    Check(again is { Name: "Trip", Layout: "kanban", Ink: "abc" } && again.Id != b.Id && n2 is { Title: "Pack", Text: "- [ ] socks" } && n2.Id != n.Id
                        && pic2.File == pic2.Id + ".png" && File.ReadAllBytes(Path.Combine(copies, pic2.File)).SequenceEqual(new byte[] { 1, 2, 3 })
                        && again.Edges is [{ Label: "then" } e] && e.From == n2.Id && e.To == pic2.Id && !File.Exists(file + ".tmp"), "board file round trip");
                }
                finally { Directory.Delete(dir, true); }
            }
            Check(new Pages.TimerPage().Content is not null && new Pages.TasksPage().Content is not null && new Pages.NotesPage().Content is not null, "timer, tasks and notes pages load");
            Check(new Pages.StartupPage().Content is not null, "startup page loads");
            {
                bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                Check(Is(Startup.Exe(@"""C:\Program Files\App\a.exe"" --min"), @"C:\Program Files\App\a.exe")
                    && Is(Startup.Exe(@"%windir%\system32\cmd.exe /x"), cmd) && Is(Startup.Exe(@"\SystemRoot\System32\cmd.exe"), cmd)
                    && Is(Startup.Exe("cmd.exe /c x"), cmd) && Is(Startup.Exe(@"C:\Gone\b.exe -x"), @"C:\Gone\b.exe") && Startup.Exe("") is null, "startup: program path from a command line");
                Check(Startup.Approved_(null) && Startup.Approved_(Startup.ApprovedValue(true)) && !Startup.Approved_(Startup.ApprovedValue(false))
                    && Startup.Approved_([6, 0, 0, 0]) && !Startup.Approved_([7, 0, 0, 0]), "startup: StartupApproved on/off bytes");
                var all = Startup.List();
                Check(all.Any(i => i.Kind == StartupKind.Task) && all.Any(i => i.Kind == StartupKind.Service), "startup: lists logon tasks and services");
            }
            {
                // A page dropped when idle must let go of app-wide events, or they keep it (and its work) alive.
                int Handlers(string name) => (typeof(App).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
                var (sampled, settings) = (Handlers("Sampled"), Handlers("SettingsChanged"));
                var merged = new Tabbed([new("Live", "", () => new Pages.SystemPage()), new("Weather", "", () => new Pages.WeatherPage())]);
                merged.Show("Weather");
                var grew = Handlers("Sampled") > sampled && Handlers("SettingsChanged") > settings;
                merged.Dispose();
                Check(grew && Handlers("Sampled") == sampled && Handlers("SettingsChanged") == settings, "dropped pages unsubscribe");
            }
            {
                // A boards file that exists but is locked is "unreadable", never "missing" (a missing one starts fresh and gets saved).
                var file = Path.Combine(Settings.Dir, "boards.json");
                Directory.CreateDirectory(Settings.Dir);
                File.WriteAllText(file, """{"All":[{"Name":"Real"}]}""");
                Exception? locked;
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) BoardStore.Load(out locked);
                var ok = BoardStore.Load(out var none).All is [{ Name: "Real" }];
                File.Delete(file);
                var fresh = BoardStore.Load(out var missing).All.Count == 1;
                Check(locked is IOException && ok && none is null && fresh && missing is null, "board: locked file is unreadable, not empty");
            }
            const string md = "# Plan\r\n- [ ] buy **milk**\r\n- [x] call [mom](https://example.com)\r\n\r\n| a | b |\r\n|---|---|\r\n| 1 | 2 |";
            var tasks = MarkdownView.TaskLines(md).ToList();
            Check(tasks.Count == 2 && !tasks[0].Done && tasks[1].Done && MarkdownView.Toggle(md, 0).Contains("- [x] buy") && MarkdownView.Toggle(md, 1).Contains("- [ ] call")
                && MarkdownView.Toggle(md, 1).Length == md.Length, "markdown: task toggle");
            Check(MarkdownView.Tags("Plan #work and #Home-2, not a#b or `#code`").OrderBy(t => t).SequenceEqual(["home-2", "work"]), $"markdown: tags {string.Join(",", MarkdownView.Tags("Plan #work and #Home-2, not a#b or `#code`"))}");
            Check(MarkdownView.Links("see [[Shopping|the list]] and [[Board 2]]").SequenceEqual(["Shopping", "Board 2"]), "markdown: wikilinks");
            Check(MarkdownView.Render("> [!warning] Careful\n> body [[x]] #t", _ => { }) is System.Windows.Controls.StackPanel { Children.Count: 1 }, "markdown: callout");
            var fixedDue = BoardStore.FixDues("- [ ] call mom @tomorrow 9\n- [ ] keep @2026-01-02 10:00\nplain @tomorrow", new DateTime(2026, 11, 18, 12, 0, 0));
            Check(fixedDue == "- [ ] call mom @2026-11-19 09:00\n- [ ] keep @2026-01-02 10:00\nplain @tomorrow", $"board task due dates: {fixedDue}");
            var exit = Pages.BoardPage.Exit(new Rect(0, 0, 100, 50), new Point(250, 25));
            Check(exit == new Point(100, 25) && Pages.BoardPage.Exit(new Rect(0, 0, 100, 50), new Point(50, -100)) == new Point(50, 0), $"arrow leaves the box at its edge: {exit}");
            {
                var dir = Path.Combine(Path.GetTempPath(), "glance-canvas-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var b = new Board { Name = "Trip" };
                    BoardItem n1 = new() { Title = "Pack", Text = "- [ ] socks", X = 10, Y = 20, W = 200, Color = "4" }, n2 = new() { Kind = "link", Text = "https://example.com/" },
                        g = new() { Kind = "group", Title = "Day 1", W = 400, H = 300 };
                    b.Items.AddRange([n1, n2, g]);
                    b.Edges.Add(new() { From = n1.Id, To = n2.Id, Label = "then" });
                    JsonCanvas.Export(b, null, Path.Combine(dir, "Trip.canvas"));
                    var again = JsonCanvas.Import(Path.Combine(dir, "Trip.canvas"));
                    var p1 = again.Items.Single(i => i.Id == n1.Id);
                    Check(again.Name == "Trip" && again.Items.Count == 3 && again.Items[0].Kind == "group" && again.Items.Single(i => i.Id == g.Id).Title == "Day 1"
                        && p1 is { Title: "Pack", Text: "- [ ] socks", X: 10, Y: 20, W: 200, Color: "4" } && again.Items.Single(i => i.Id == n2.Id) is { Kind: "link", Text: "https://example.com/" }
                        && again.Edges is [{ Label: "then" } e] && e.From == n1.Id && e.To == n2.Id, "json canvas round trip");
                }
                finally { Directory.Delete(dir, true); }
            }
            Check(MarkdownView.Render(md, _ => { }) is System.Windows.Controls.StackPanel { Children.Count: 3 }, "markdown: heading, list, table"); // XAML parses, saved board reads
            HashSet<object> Keys(string f) => [.. new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{f}.xaml") }.Keys];
            Check(Keys("Mint").SetEquals(Keys("Flamingo")), "themes define the same keys");

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
            var gpuUse = new Native.GpuUse();
            gpuUse.Read(); // the first sample only sets the baseline
            Thread.Sleep(300);
            var gpu = gpuUse.Read();
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

            // Notes: everything a note (and its sticky) holds survives a save and load (an absolute name keeps it out of AppData)
            var tmp = Path.Combine(Path.GetTempPath(), $"glance-selftest-{Guid.NewGuid():N}.json");
            try
            {
                var store = new JsonStore<Note>(tmp);
                Check(store.Load(), "store: no file yet");
                var sticky = new Note { Title = "t", Kind = NoteKind.Checklist, Color = "Blue", Opacity = 0.5, Left = -10, Width = 300, Sticky = true, Font = "Consolas", FontSize = 18 };
                sticky.Items.Add(new TodoItem { Text = "a", Done = true, Due = new DateTime(2026, 1, 2, 3, 4, 0), Remind = true, Reminded = true });
                store.Items.Add(sticky);
                store.Items.Add(new Note()); // never placed: no position yet
                sticky.Title = "t"; // unchanged: not an edit
                Check(sticky.Edited is null, "note: no edit, no Edited");
                sticky.Items.Add(new TodoItem { Text = "b" });
                Check(sticky.Edited is not null && sticky.Meta.EndsWith("1/2 done") && sticky.Summary == "1/2 done · b", "note: an item edit stamps Edited");
                store.Flush();
                var reread = new JsonStore<Note>(tmp);
                Check(!reread.Load() && reread.Items.Count == 2, "store: reload");
                Check(reread.Items[0] is { Title: "t", Kind: NoteKind.Checklist, Color: "Blue", Opacity: 0.5, Left: -10, Top: null, Width: 300, Sticky: true, Font: "Consolas", FontSize: 18, Edited: not null }
                      && reread.Items[0].Items[0] is { Text: "a", Done: true, Remind: true, Reminded: true } && reread.Items[1] is { Left: null, Sticky: false },
                    "store: note and sticky round-trip");
                // stickies.json: "Shown" becomes Sticky (on when missing), and is saved under the new name
                var old = System.Text.Json.JsonSerializer.Deserialize<List<NoteStore.OldSticky>>("""[{"Title":"a","Shown":false},{"Title":"b"}]""")!;
                var json = System.Text.Json.JsonSerializer.Serialize<List<Note>>([.. old]);
                Check(old is [{ Sticky: false }, { Sticky: true }] && !json.Contains("Shown") && json.Contains("\"Sticky\":true"),
                    "stickies.json: migrates to notes");
                Check(reread.Items[1] is { Font: Note.DefaultFont, FontSize: 14 } && new Note { FontSize = 99, Font = " " } is { FontSize: 40, Font: Note.DefaultFont },
                    "note: default font, size clamped");
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

            // Media: trim, resize, audio out of video, lossless keeps the sample rate (real files through Windows' transcoder)
            Task.Run(Media).GetAwaiter().GetResult();
            Check(MediaOps.ParseTime("1:05.5") == TimeSpan.FromSeconds(65.5) && MediaOps.ParseTime("1:02:03") == new TimeSpan(1, 2, 3)
                && MediaOps.ParseTime("90") == TimeSpan.FromSeconds(90) && MediaOps.ParseTime("x") is null && MediaOps.ParseTime("-1") is null, "media time parse");

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
            {
                Pages.CliPane[] ps = [new() { Dir = "" }, new() { Dir = "" }, new() { Dir = "" }];
                var root = new Pages.CliNode { Pane = ps[0] };
                root.Split(ps[0], ps[1], "left");
                root.Split(ps[0], ps[2], "down");
                Check(root is { Column: false, B.Column: true } && root.A!.Pane == ps[1] && root.B.A!.Pane == ps[0] && root.B.B!.Pane == ps[2],
                    "cli split: left of, then below");
                Check(root.Without(ps[0]) is { Pane: null } rest && rest.A!.Pane == ps[1] && rest.B!.Pane == ps[2], "cli unsplit collapses");
                Check(new Pages.CliNode { Pane = ps[0] }.Without(ps[0]) is null, "cli last pane");
            }

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
            DiskChecks();

            static string Rel(string tag, string asset, string? digest) =>
                $$"""{"tag_name":"{{tag}}","assets":[{"name":"{{asset}}","browser_download_url":"https://x/{{asset}}"{{(digest is null ? "" : $",\"digest\":\"{digest}\"")}}}]}""";
            var v110 = new Version(1, 1, 0);
            var up = Updater.Newer(Rel("v1.1.1", "Glance-1.1.1-win-x64.zip", "sha256:ab12"), v110);
            Check(up is { Sha256: "ab12", Url: "https://x/Glance-1.1.1-win-x64.zip" } && up.Version == new Version(1, 1, 1), "update: newer patch offered");
            Check(Updater.Newer(Rel("v1.1.0", "Glance-1.1.0-win-x64.zip", "sha256:ab12"), new Version(1, 1, 0, 0)) is null, "update: same version (4-part) not offered");
            Check(Updater.Newer(Rel("v1.0.9", "Glance-1.0.9-win-x64.zip", "sha256:ab12"), v110) is null, "update: older not offered");
            Check(Updater.Newer(Rel("v1.2.0", "Glance-1.2.0-win-x64.zip", null), v110) is null, "update: no digest, not offered");
            Check(Updater.Newer(Rel("v1.2.0", "notes.txt", "sha256:ab12"), v110) is null, "update: no zip, not offered");
            const string both = """{"tag_name":"v1.2.0","assets":[{"name":"Glance-1.2.0-win-x64.zip","browser_download_url":"https://x/z","digest":"sha256:aa"},{"name":"Glance-1.2.0-setup.exe","browser_download_url":"https://x/Glance-1.2.0-setup.exe","digest":"sha256:bb"}]}""";
            Check(Updater.Newer(both, v110, installed: true) is { Setup: true, Sha256: "bb" }, "update: installed copy takes the setup");
            Check(Updater.Newer(both, v110) is { Setup: false, Sha256: "aa" }, "update: portable copy takes the zip");
            Check(Updater.Newer(Rel("v1.2.0", "Glance-1.2.0-win-x64.zip", "sha256:ab12"), v110, installed: true) is { Setup: false },
                "update: installed copy falls back to the zip when a release has no setup");

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
        finally
        {
            BoardStore.Save(); // stops its save timer, which would otherwise fire during shutdown and recreate the folder
            try { Directory.Delete(Settings.Dir, true); } catch (IOException) { }
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
            Check(Units.Bytes(1536) == "2 KB" && Units.Bytes(5L << 30) == "5.0 GB" && Units.Bytes(1863L << 30) == "1.8 TB", "size text");
            Check(Units.Megabytes(512) == "512 MB" && Units.Megabytes(1536) == "1.5 GB" && Units.Rate(2_500_000) == "2.5 MB/s", "memory and rate text");

            // Debounce: a burst of pokes runs once; nothing pending, nothing runs
            var runs = 0;
            var debounce = new Debounce(TimeSpan.FromHours(1), () => runs++);
            debounce.Flush();
            debounce.Poke(); debounce.Poke(); debounce.Flush(); debounce.Flush();
            debounce.Poke(); debounce.Cancel(); debounce.Flush();
            Check(runs == 1, "debounce: one run per burst, none after cancel");

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

    // The Disk page: the cleaner only ever takes old files from inside its root, and the scan adds up right.
    static void DiskChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"glance-selftest-disk-{Environment.ProcessId}");
        var outside = root + "-outside";
        var link = Path.Combine(root, "link");
        string Put(string dir, string rel, int bytes, int ageHours = 48)
        {
            var p = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[bytes]);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddHours(-ageHours));
            return p;
        }
        try
        {
            var old = Put(root, "a.tmp", 1000);
            Put(root, @"sub\deep\b.tmp", 2000);
            var young = Put(root, @"keep\c.tmp", 4000, ageHours: 1);
            var precious = Put(outside, "precious.txt", 500);
            // A junction out of the sandbox (no privilege needed, unlike a symlink): what it points at must survive.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
                { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
            Check(File.Exists(Path.Combine(link, "precious.txt")), "disk: junction made");

            var tree = DiskScan.Walk(root);
            Check(tree.Size == 7000 && tree.Kids!.Count == 3 && tree.Kids[0].Name == "keep" && tree.Kids[2] is { Lump: true, Size: 1000 },
                $"disk walk: {tree.Size} in {tree.Kids!.Count}"); // keep, sub, small files; the junction is not followed
            Check(Cleaner.Roots(new(Path.Combine(root, "*", "deep"))).SequenceEqual([Path.Combine(root, "sub", "deep")]), "clean: * is each folder");

            var cutoff = DateTime.UtcNow.AddHours(-24);
            long bytes = 0;
            var skipped = 0;
            Cleaner.Sweep(root, null, cutoff, false, true, ref bytes, ref skipped);
            Check(bytes == 3000 && File.Exists(old), $"clean: estimate {bytes}");
            bytes = 0;
            Cleaner.Sweep(root, null, cutoff, true, true, ref bytes, ref skipped);
            Check(bytes == 3000 && skipped == 0, $"clean: freed {bytes}, skipped {skipped}");
            Check(!File.Exists(old) && !Directory.Exists(Path.Combine(root, "sub")) && File.Exists(young) && Directory.Exists(root),
                "clean: old files and their empty folders go; young files and the root stay");
            Check(File.Exists(precious) && Directory.Exists(link), "clean: nothing is reached through a junction");

            var thumb = Put(root, "thumbcache_96.db", 10);
            var other = Put(root, "other.db", 10);
            Cleaner.Sweep(root, "thumbcache_*.db", DateTime.UtcNow, true, true, ref bytes, ref skipped);
            Check(!File.Exists(thumb) && File.Exists(other) && File.Exists(young), "clean: a pattern takes only its files, from that folder only");

            string Home(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
            Check(!Cleaner.Safe("D3DSCache") && !Cleaner.Safe(@"C:\") && !Cleaner.Safe(@"C:\Windows") && !Cleaner.Safe(@"C:\Windows\..\Users")
                && !Cleaner.Safe(Home(Environment.SpecialFolder.UserProfile)) && !Cleaner.Safe(Home(Environment.SpecialFolder.LocalApplicationData))
                && !Cleaner.Safe(@"C:\", pattern: true), "clean: refuses relative paths, drive roots and the profile folders");
            Check(Cleaner.Safe(@"C:\Windows\Temp") && Cleaner.Safe(@"C:\Windows", pattern: true) && Cleaner.Safe(root), "clean: allows real targets");
            Check(Cleaner.Items.Select(i => i.Id).Distinct().Count() == Cleaner.Items.Length, "clean: ids are unique");
            Check(Cleaner.Items.All(i => i.Admin == (i.Group == "System")) && Cleaner.Run(["no-such-item"]) == 0, "clean: an unknown id cleans nothing");
            Check(Cleaner.Estimate(Cleaner.Items.First(i => i.Id == Cleaner.Bin)).Bytes >= 0, "clean: recycle bin size");
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link); // the junction itself, not what it points at
            foreach (var d in new[] { root, outside }) if (Directory.Exists(d)) Directory.Delete(d, true);
        }

        var scan = DiskScan.Parse("""
            "Name","Files","Folders","Logical Size","Physical Size","Attributes","Last Change","WinDirStat Attributes","Index"
            "X:\",4,2,5000010,5005312,"",2026-11-13T16:59:02Z,0x30000004,0x0000000000000000
            "X:\a",2,1,3000000,3002368,"",2026-11-13T16:59:02Z,0x20000004,0x0000000000000000
            "X:\a\big, one.bin",0,0,2999000,3000000,"A",2026-11-13T16:59:02Z,0x20000008,0x0000000000000000
            "X:\a\sub",1,0,1000,2368,"",2026-11-13T16:59:02Z,0x20000004,0x0000000000000000
            "X:\a\sub\tiny.txt",0,0,1000,2368,"A",2026-11-13T16:59:02Z,0x20000008,0x0000000000000000
            "X:\b.iso",0,0,2000000,2002944,"A",2026-11-13T16:59:02Z,0x20000008,0x0000000000000000
            "X:\c.txt",0,0,10,0,"A",2026-11-13T16:59:02Z,0x20000008,0x0000000000000000
            """.Split('\n').Select(l => l.TrimEnd('\r')))!;
        var a = scan.Kids![0];
        Check(scan is { Name: @"X:\", Size: 5005312 } && scan.Kids.Count == 3 && a is { Name: "a", Size: 3002368 } && scan.Kids[1].Path == @"X:\b.iso"
            && scan.Kids[2].Lump, $"scan csv: {string.Join(", ", scan.Kids.Select(k => k.Name))}");
        Check(a.Kids![0] is { Name: "big, one.bin", Size: 3000000, IsDir: false } && a.Kids[1] is { Name: "sub", IsDir: true }
            && a.Kids[1].Kids!.Single() is { Lump: true, Size: 2368 } && a.Kids[0].Path == @"X:\a\big, one.bin", "scan csv: folders, a comma in a name, small files lumped");
        var kinds = DiskScan.Kinds(scan);
        Check(kinds.SequenceEqual([("Apps and libraries", 3000000L), ("Archives and disk images", 2002944L), (DiskScan.SmallKind, 2368L)])
            && kinds.Sum(k => k.Bytes) == scan.Size, $"scan kinds: {string.Join(", ", kinds)}");
        DiskScan.Remove(a.Kids[0]);
        Check(a.Size == 2368 && scan.Size == 2005312 && scan.Kids[0].Name == "b.iso", $"scan: recycled item leaves the tree: {a.Size} {scan.Size}");

        var area = new Rect(0, 0, 6, 4);
        var cells = Treemap.Squarify([6, 6, 4, 3, 2, 2, 1], area); // the example from the squarified treemap paper
        Check(cells[0] == new Rect(0, 0, 3, 2) && cells[1] == new Rect(0, 2, 3, 2), $"treemap: first row {cells[0]} {cells[1]}");
        Check(cells.Zip(new double[] { 6, 6, 4, 3, 2, 2, 1 }).All(c => Math.Abs(c.First.Width * c.First.Height - c.Second) < 1e-9
            && c.First.X >= -1e-9 && c.First.Y >= -1e-9 && c.First.Right <= 6 + 1e-9 && c.First.Bottom <= 4 + 1e-9), "treemap: areas match sizes, inside the bounds");
        Check(Treemap.Squarify([], area).Length == 0 && Treemap.Squarify([0, 0], area).All(r => r == default), "treemap: nothing to lay out");

        // Both pages and their row templates build and lay out (a bad resource name only shows up at runtime)
        var clean = new Pages.CleanPage();
        var disk = new Pages.ScanPage();
        disk.Show(scan);
        foreach (var page in new System.Windows.Controls.UserControl[] { clean, disk, new Pages.MediaPage() })
        {
            page.Measure(new Size(1000, 700));
            page.Arrange(new Rect(0, 0, 1000, 700));
            page.UpdateLayout();
        }
        Check(clean.Groups.ItemContainerGenerator.ContainerFromIndex(0) is not null, "clean page: groups built");
        Check(disk.List.ItemContainerGenerator.ContainerFromIndex(1) is not null && disk.Map.ActualWidth > 100, "scan page: rows and treemap laid out");
    }

    static async Task Media()
    {
        var dir = Directory.CreateTempSubdirectory("glance-selftest-").FullName;
        try
        {
            // 2 s of 440 Hz, 44.1 kHz 16-bit stereo
            const int rate = 44100, seconds = 2;
            var wav = Path.Combine(dir, "tone.wav");
            using (var w = new BinaryWriter(File.Create(wav)))
            {
                var data = rate * seconds * 4;
                w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
                w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(data);
                for (var i = 0; i < rate * seconds; i++) { var v = (short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 8000); w.Write(v); w.Write(v); }
            }
            var tone = await MediaOps.Probe(wav);
            Check(tone.Video is null && tone.Audio?.SampleRate == rate && Math.Abs(tone.Duration.TotalSeconds - seconds) < 0.1, $"media probe wav: {tone.Duration}");

            static bool Near(TimeSpan t, double s) => Math.Abs(t.TotalSeconds - s) < 0.2;
            var f = (string n) => MediaOps.Formats.First(x => x.Name == n);

            var mp3 = Path.Combine(dir, "cut.mp3");
            await MediaOps.Convert(wav, mp3, MediaOps.Profile(f("MP3"), MediaOps.Quality.Medium, tone, null),
                TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5), tone.Duration, null, default);
            var cut = await MediaOps.Probe(mp3);
            Check(cut.Audio is not null && Near(cut.Duration, 1), $"media trim to mp3: {cut.Duration}");

            var flac = Path.Combine(dir, "same.flac");
            await MediaOps.Convert(wav, flac, MediaOps.Profile(f("FLAC"), MediaOps.Quality.High, tone, null), TimeSpan.Zero, tone.Duration, tone.Duration, null, default);
            Check((await MediaOps.Probe(flac)).Audio?.SampleRate == rate, "media flac keeps sample rate");
            var wav2 = Path.Combine(dir, "cut.wav");
            await MediaOps.Convert(wav, wav2, MediaOps.Profile(f("WAV"), MediaOps.Quality.High, tone, null), TimeSpan.FromSeconds(1), tone.Duration, tone.Duration, null, default);
            var wavInfo = await MediaOps.Probe(wav2);
            Check(wavInfo.Audio?.SampleRate == rate && Near(wavInfo.Duration, 1), $"media wav keeps sample rate, trims start: {wavInfo.Duration}");

            var bad = Path.Combine(dir, "nothing.mp3");
            await File.WriteAllTextAsync(bad, "not audio");
            var refused = false;
            try { await MediaOps.Convert(bad, Path.Combine(dir, "out.m4a"), MediaOps.Profile(f("M4A"), MediaOps.Quality.Low, tone, null), TimeSpan.Zero, tone.Duration, tone.Duration, null, default); }
            catch (Exception) { refused = true; }
            Check(refused && !File.Exists(Path.Combine(dir, "out.m4a")), "media: a failed convert leaves no file");
            try { await MediaOps.Convert(wav, wav, MediaOps.Profile(f("WAV"), MediaOps.Quality.High, tone, null), TimeSpan.Zero, tone.Duration, tone.Duration, null, default); refused = false; }
            catch (IOException) { refused = true; }
            Check(refused && File.Exists(wav), "media: won't write over its own source");

            // A 640×360 video with that tone under it, made with Windows' own editor
            var comp = new Windows.Media.Editing.MediaComposition();
            comp.Clips.Add(Windows.Media.Editing.MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 30, 140, 90), TimeSpan.FromSeconds(seconds)));
            comp.BackgroundAudioTracks.Add(await Windows.Media.Editing.BackgroundAudioTrack.CreateFromFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(wav)));
            var mp4 = Path.Combine(dir, "clip.mp4");
            await File.WriteAllBytesAsync(mp4, []);
            var make = Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(Windows.Media.MediaProperties.VideoEncodingQuality.Vga);
            (make.Video.Width, make.Video.Height) = (640, 360);
            await comp.RenderToFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(mp4), Windows.Media.Editing.MediaTrimmingPreference.Precise, make);
            var clip = await MediaOps.Probe(mp4);
            Check(clip.Video is { Width: 640, Height: 360 } && clip.Audio is not null, $"media probe mp4: {clip.Video?.Width}×{clip.Video?.Height}");

            var small = Path.Combine(dir, "small.mp4");
            await MediaOps.Convert(mp4, small, MediaOps.Profile(f("MP4"), MediaOps.Quality.Low, clip, 240), TimeSpan.Zero, TimeSpan.FromSeconds(1), clip.Duration, null, default);
            var smallInfo = await MediaOps.Probe(small);
            Check(smallInfo.Video is { Width: 426, Height: 240 } && Near(smallInfo.Duration, 1), $"media resize + trim: {smallInfo.Video?.Width}×{smallInfo.Video?.Height} {smallInfo.Duration}");

            var sound = Path.Combine(dir, "sound.m4a");
            await MediaOps.Convert(mp4, sound, MediaOps.Profile(f("M4A"), MediaOps.Quality.High, clip, null), TimeSpan.Zero, clip.Duration, clip.Duration, null, default);
            var soundInfo = await MediaOps.Probe(sound);
            Check(soundInfo.Video is null && soundInfo.Audio is not null && Near(soundInfo.Duration, seconds), "media: audio out of video");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    static Snapshot Snap(float gpuTemp) => new("", 0, null, null, null, "", 0, gpuTemp, null, null, null, null, null, null, 0, 0, [], []);

    static int checks;

    static void Check(bool ok, string what)
    {
        checks++;
        if (!ok) throw new Exception("selftest failed: " + what);
    }
}
