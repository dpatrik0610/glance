using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Pages;
using Glance.Services;
using Forms = System.Windows.Forms;

namespace Glance;

public partial class App : Application
{
    public static Settings Settings { get; } = Settings.Load();

    /// Raised on the sampler thread for every sample. Drives/Procs are only filled while <see cref="Live"/>.
    public static event Action<Snapshot>? Sampled;
    public static event Action? SettingsChanged;

    static volatile bool live;
    static readonly SemaphoreSlim wake = new(0);
    static readonly Alerts alerts = new();

    /// True while the System page is on screen: fast sampling with per-process data.
    /// False: a light sample every 5 s for the tray icon and alerts (metrics still logged once a minute).
    public static bool Live
    {
        get => live;
        set { live = value; if (value) Wake(); }
    }

    /// Result of the last hotkey registration, for the Settings page.
    public static string HotkeyStatus { get; private set; } = "";

    const int HotkeyId = 1;
    Mutex? mutex;
    Tray? tray;
    MainWindow? main;
    BarWindow? bar;
    IntPtr hwnd;
    string? registeredHotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest")) { Shutdown(SelfTest.Run()); return; }
        // The Clean page's elevated half: `--clean id,id`. The megabytes freed go back as the exit code.
        if (Array.IndexOf(e.Args, "--clean") is >= 0 and var c) { Shutdown(Cleaner.Run(e.Args.ElementAtOrDefault(c + 1)?.Split(',') ?? [])); return; }

        bool first;
        try
        {
            mutex = new Mutex(false, "Glance.Instance");
            first = mutex.WaitOne(e.Args.Contains("--wait") ? 5000 : 0);
        }
        catch (AbandonedMutexException) { first = true; } // previous instance died holding it
        catch (UnauthorizedAccessException) { first = false; } // the running one is elevated
        if (!first) { Send(e.Args); Shutdown(); return; } // second launch hands its args to the running one
        if (e.Args.Contains("--exit")) { Shutdown(); return; } // nothing running to close

        Db.Init(Settings.MongoUrl);
        NoteStore.Load(); // at startup, not when the Notes page opens: reminders need it
        Db.Info($"start admin={Native.IsAdmin}");
        DispatcherUnhandledException += (_, a) => { Db.Error("ui", a.Exception); a.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, a) => Db.Error("task", a.Exception);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowMain());
        var stickies = new Forms.ToolStripMenuItem("Show stickies") { CheckOnClick = true };
        stickies.Click += (_, _) => Stickies.SetShown(stickies.Checked);
        menu.Opening += (_, _) => stickies.Checked = Settings.StickiesShown;
        menu.Items.Add(stickies);
        if (!Native.IsAdmin) menu.Items.Add("Restart as admin (CPU temps)", null, (_, _) => RestartAsAdmin());
        if (DeployScript is not null) menu.Items.Add("Rebuild and restart", null, (_, _) => Rebuild());
        else menu.Items.Add("Check for updates", null, (_, _) => CheckUpdate(manual: true));
        menu.Items.Add("Exit", null, (_, _) => Quit());
        tray = new Tray(menu, () => ToggleMain(false));

        main = new MainWindow();
        main.DroppedChanged += on => bar?.Dropped(on);
        // The window may start hidden (--tray), but the hotkey and clipboard listener need its handle now.
        hwnd = new WindowInteropHelper(main).EnsureHandle();
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        Native.AddClipboardFormatListener(hwnd);
        RegisterHotkey();
        SettingsChanged += RegisterHotkey;
        ApplyBar();
        SettingsChanged += ApplyBar;
        Stickies.Load(); // after the main window exists, so it stays Application.MainWindow

        Sampled += s => Dispatcher.BeginInvoke(() => tray.Update(s));
        alerts.Fired += a => Task.Run(async () =>
        {
            var who = a.Blame == Blame.None ? null : await HardwareMonitor.Top(a.Blame == Blame.Memory);
            _ = Dispatcher.BeginInvoke(() => ShowAlert(a, who));
        });

        Media.TrackChanged += ShowTrack;
        Media.Start();

        Listen();
        Task.Run(SampleLoop);
        new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => CheckReminders(), Dispatcher);
        Dispatcher.BeginInvoke(CheckReminders, DispatcherPriority.ApplicationIdle); // catch up right after start
        // Google Calendar's next two days, for its reminder popups (nothing happens until it's connected).
        new DispatcherTimer(TimeSpan.FromMinutes(5), DispatcherPriority.Background, (_, _) => _ = GoogleCalendar.RefreshUpcoming(), Dispatcher);
        _ = GoogleCalendar.RefreshUpcoming();
        // Re-point the registry at this exe, in case the folder moved.
        if (Settings.ExplorerMenu) Settings.ExplorerMenu = true;
        if (Settings.AutoStart) Settings.AutoStart = true;
        // Released builds look for a newer release at start and daily; a build from source updates by "Rebuild and restart".
        if (DeployScript is null)
        {
            new DispatcherTimer(TimeSpan.FromHours(24), DispatcherPriority.Background, (_, _) => CheckUpdate(), Dispatcher);
            CheckUpdate();
        }

        if (CliArg(e.Args) is { } dir) OpenCli(dir);
        else if (!e.Args.Contains("--tray")) ShowMain();
    }

    // Single-instance IPC: a second launch sends its args here. Only this user may write to the pipe.
    static readonly string PipeName = "Glance." + Environment.UserName;

    static void Send(string[] args)
    {
        try
        {
            Native.AllowSetForegroundWindow(-1 /* ASFW_ANY */); // we got focus rights from Explorer; pass them on so the running one can come to front
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            using var w = new StreamWriter(pipe);
            w.Write(string.Join('\n', args));
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { } // nothing to hand to
    }

    void Listen() => Task.Run(async () =>
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (true)
        {
            try
            {
                await using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, security);
                await pipe.WaitForConnectionAsync();
                var args = (await new StreamReader(pipe).ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (args.Contains("--exit")) Quit(ask: false); // from tools\deploy.ps1
                    else if (CliArg(args) is { } dir) OpenCli(dir);
                    else ShowMain();
                });
            }
            catch (IOException e) { Db.Error("ipc", e); await Task.Delay(1000); }
        }
    });

    // `--cli "<folder>"` from the Explorer menu. Explorer quotes a drive root as "C:\" which arrives as C:" (backslash-quote escape).
    internal static string? CliArg(string[] args)
    {
        var i = Array.IndexOf(args, "--cli");
        if (i < 0) return null;
        var dir = i + 1 < args.Length ? args[i + 1].TrimEnd('"') : "";
        return dir.EndsWith(':') ? dir + "\\" : dir;
    }

    internal void OpenCli(string dir, string? command = null, string? name = null)
    {
        ShowMain();
        ((CliPage)main!.ShowTab("CLI")).Open(dir, command, name);
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && (int)w == HotkeyId) ToggleMain(true);
        else if (msg == Native.WM_CLIPBOARDUPDATE) ClipboardHistory.Capture();
        return IntPtr.Zero;
    }

    void RegisterHotkey()
    {
        if (Settings.Hotkey == registeredHotkey) return;
        registeredHotkey = Settings.Hotkey;
        Native.UnregisterHotKey(hwnd, HotkeyId);
        if (!Native.TryParseHotkey(Settings.Hotkey, out var mods, out var vk))
            HotkeyStatus = "Not a valid shortcut.";
        else if (!Native.RegisterHotKey(hwnd, HotkeyId, mods | 0x4000 /* MOD_NOREPEAT */, vk))
            HotkeyStatus = "Taken by another app.";
        else
            HotkeyStatus = "Active.";
        if (HotkeyStatus != "Active.") Db.Warn($"hotkey {Settings.Hotkey}: {HotkeyStatus}");
    }

    public static void ShowAlert(Alert a, Proc? who = null)
    {
        var blame = who is null ? "" : " · " + (a.Blame == Blame.Memory ? $"{who.Name} {who.MemText}" : $"{who.Name} {who.Cpu:0}%");
        Db.Warn($"alert {a.Label} {a.Value:0}{a.Unit} (limit {a.Limit:0}){blame}");
        var color = a.Critical ? Color.FromRgb(0xFF, 0x4D, 0x5E) : Color.FromRgb(0xFF, 0x6A, 0x3D);
        AlertWindow.Show(new Toast(a.Label + blame, a.Value.ToString("0"), a.Unit, "Limit", $"{(a.Above ? "≥" : "≤")} {a.Limit:0}{a.Unit}", color, false),
            ((App)Current).ShowMain);
    }

    void ShowTrack(Player p)
    {
        if (!Settings.TrackAlerts) return;
        AlertWindow.Show(new Toast($"Now playing · {p.Source}", p.Title, "", "", "", Color.FromRgb(0xFF, 0x6A, 0x3D), false, p.Cover, p.Artist, Quiet: true),
            () => Drop("Now Playing"));
    }

    // Checklist reminders: polled, so ones that came due while Glance was off fire right after start.
    void CheckReminders()
    {
        foreach (var (note, item) in NoteStore.DueReminders(DateTime.Now).ToList())
        {
            item.Reminded = true; // saved with its note
            Db.Info($"reminder {note.DisplayTitle}: {item.Text}");
            AlertWindow.Show(new Toast($"Reminder · {note.DisplayTitle}", item.Text.Length > 0 ? item.Text : "(no text)", "",
                "Due", item.Due!.Value.ToString("HH:mm"), Color.FromRgb(0x5A, 0xA9, 0xFF), true), () =>
            {
                if (note is Sticky sticky) { Stickies.Reveal(sticky); return; }
                ShowMain();
                main!.ShowTab("Notes");
                NoteStore.RequestFocus(note);
            });
        }
        foreach (var (ev, minutes) in GoogleCalendar.DueAlerts(DateTime.Now))
        {
            Db.Info($"calendar reminder: {ev.Title}");
            var when = ev.AllDay ? ev.Start.ToString("ddd d MMM") : minutes == 0 ? "Now" : ev.Start.ToString("HH:mm");
            AlertWindow.Show(new Toast($"Calendar · {ev.Calendar.Name}", ev.Title, "", ev.AllDay ? "All day" : "Starts", when, ev.Calendar.Color, true,
                Detail: ev.Location), () =>
            {
                ShowMain();
                main!.ShowTab("Calendar");
            });
        }
    }

    static DispatcherTimer? saveTimer;

    /// Applies immediately; the disk write is debounced so slider drags don't hammer the file.
    public static void ApplySettings()
    {
        SettingsChanged?.Invoke();
        saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => { saveTimer!.Stop(); Settings.Save(); }, Current.Dispatcher);
        saveTimer.Stop();
        saveTimer.Start();
    }

    static void Wake()
    {
        if (wake.CurrentCount == 0) wake.Release();
    }

    static async Task SampleLoop()
    {
        HardwareMonitor hw;
        try { hw = new HardwareMonitor(); }
        catch (Exception e) { Db.Error("sensor init", e); return; }

        var sw = Stopwatch.StartNew();
        var nextMetric = TimeSpan.Zero;
        while (true)
        {
            var isLive = live;
            try
            {
                var s = hw.Read(isLive);
                Sampled?.Invoke(s);
                alerts.Check(s, Settings, DateTime.UtcNow);
                if (sw.Elapsed >= nextMetric)
                {
                    Db.Metric(s, hw.Minute());
                    nextMetric = sw.Elapsed + TimeSpan.FromMinutes(1);
                }
            }
            catch (Exception e) { Db.Error("sample", e); }
            await wake.WaitAsync(isLive ? Math.Max(250, Settings.RefreshMs) : 5000);
        }
    }

    void ApplyBar()
    {
        if (Settings.TopBar && bar is null)
        {
            bar = new BarWindow(Drop);
            bar.Show();
        }
        else if (!Settings.TopBar && bar is not null)
        {
            bar.Close();
            bar = null;
        }
    }

    /// From the bar: Glance slides down under it, at that tab (null: where it was).
    void Drop(string? tab)
    {
        if (!main!.IsVisible || main.Dropped) main.DropUnder(bar!.Edge); // already open somewhere else: just switch tabs
        ShowMain();
        if (tab is not null) main.ShowTab(tab);
    }

    /// Where alerts hang: just under the bar, or at the top edge when the bar is off or stepped aside.
    internal static double AlertTop => ((App)Current).bar is { IsVisible: true } b ? b.Edge + 4 : SystemParameters.WorkArea.Top + 8;

    void ShowMain()
    {
        var wasHidden = !main!.IsVisible;
        main.Show();
        if (wasHidden) main.Reveal();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        // Activate alone can't lift an already-visible window above others (foreground lock); a topmost blip can.
        main.Topmost = true;
        main.Topmost = Settings.Topmost;
        main.Activate();
    }

    // Hotkey: a visible-but-buried window comes to front instead of hiding. Tray clicks always steal focus, so they ignore it.
    // Window.IsActive can be true while another app has the foreground, so ask Windows directly.
    void ToggleMain(bool requireFocus)
    {
        if (main!.IsVisible && (!requireFocus || Native.GetForegroundWindow() == hwnd)) main.Away(); else ShowMain();
    }

    void RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--wait") { UseShellExecute = true, Verb = "runas" });
            Quit();
        }
        catch (System.ComponentModel.Win32Exception) { } // UAC prompt declined
    }

    /// tools\deploy.ps1 in the source tree this exe was built from; null for a build whose source isn't on this PC.
    internal static readonly string? DeployScript =
        typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SourceDir")?.Value is { } src
        && Path.Combine(src, "tools", "deploy.ps1") is var script && File.Exists(script) ? script : null;

    /// Builds the source in a console window, then the script closes this instance and starts the new build.
    internal static void Rebuild()
    {
        if (DeployScript is null) return;
        Process.Start(new ProcessStartInfo(Shell.PowerShell, $"-NoProfile -ExecutionPolicy Bypass -File \"{DeployScript}\"") { UseShellExecute = true });
        Db.Info("rebuild requested");
    }

    Version? offered; // once per run per version: closing the alert means "not now"

    /// Settings, the tray and Ctrl+K: released builds only (a build from source updates by "Rebuild and restart").
    internal static void CheckForUpdates() => ((App)Current).CheckUpdate(manual: true);

    // Automatic: a newer release shows an alert, once per run per version. Asked for: straight to the update's
    // confirmation, and it says so when there's nothing new or the check failed.
    async void CheckUpdate(bool manual = false)
    {
        try
        {
            var r = await Updater.Check();
            if (manual)
            {
                if (r is null) Dialog.Info("Update", "Glance is up to date", $"Version {Updater.Current}");
                else Update(r);
                return;
            }
            if (r is null || r.Version == offered) return;
            offered = r.Version;
            AlertWindow.Show(new Toast("Glance · update", $"Version {r.Version}", "", "You have", Updater.Current.ToString(),
                Color.FromRgb(0xFF, 0x6A, 0x3D), true, Detail: "Click to update and restart"), () => Update(r));
        }
        catch (Exception e)
        {
            Db.Warn($"update check: {e.Message}"); // offline, rate-limited: try again tomorrow
            if (manual) Dialog.Info("Update", "Couldn't check for updates", e.Message);
        }
    }

    async void Update(Updater.Release r)
    {
        if (!Dialog.Confirm("Update", $"Update Glance to {r.Version}?",
                "Glance downloads it, closes and starts again." +
                (CliPage.Running > 0 ? " Terminals reopen in their folders, but anything running in them stops." : ""),
                "Update", danger: false)) return;
        AlertWindow.Show(new Toast("Glance · update", $"Downloading {r.Version}", "", "", "", Color.FromRgb(0xFF, 0x6A, 0x3D), false,
            Detail: "Glance restarts when it's ready", Quiet: true), () => { });
        try
        {
            await Updater.Install(r);
            Db.Info($"updating to {r.Version}");
            Quit(ask: false);
        }
        catch (Exception e)
        {
            Db.Error("update", e);
            Dialog.Info("Update", "The update failed", e.Message);
        }
    }

    void Quit(bool ask = true)
    {
        if (ask && CliPage.Running > 0 && !Dialog.Confirm("Exit Glance",
                $"{CliPage.Running} terminal{(CliPage.Running == 1 ? " is" : "s are")} still open",
                "They reopen next time, but anything running in them stops.", "Exit")) return;
        Db.Info("exit");
        if (saveTimer?.IsEnabled == true) Settings.Save();
        tray!.Hide();
        mutex?.ReleaseMutex();
        Shutdown(); // pages flush their own unsaved state on Application.Exit
    }
}
