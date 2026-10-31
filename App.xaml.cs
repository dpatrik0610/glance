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
    IntPtr hwnd;
    string? registeredHotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest")) { Shutdown(SelfTest.Run()); return; }

        bool first;
        try
        {
            mutex = new Mutex(false, "Glance.Instance");
            first = mutex.WaitOne(e.Args.Contains("--wait") ? 5000 : 0);
        }
        catch (AbandonedMutexException) { first = true; } // previous instance died holding it
        catch (UnauthorizedAccessException) { first = false; } // the running one is elevated
        if (!first) { Send(e.Args); Shutdown(); return; } // second launch hands its args to the running one

        Db.Init(Settings.MongoUrl);
        NoteStore.Load(); // at startup, not when the Notes page opens: reminders need it
        Db.Info($"start admin={Native.IsAdmin}");
        DispatcherUnhandledException += (_, a) => { Db.Error("ui", a.Exception); a.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, a) => Db.Error("task", a.Exception);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowMain());
        if (!Native.IsAdmin) menu.Items.Add("Restart as admin (CPU temps)", null, (_, _) => RestartAsAdmin());
        menu.Items.Add("Exit", null, (_, _) => Quit());
        tray = new Tray(menu, () => ToggleMain(false));

        main = new MainWindow();
        // The window may start hidden (--tray), but the hotkey and clipboard listener need its handle now.
        hwnd = new WindowInteropHelper(main).EnsureHandle();
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        Native.AddClipboardFormatListener(hwnd);
        RegisterHotkey();
        SettingsChanged += RegisterHotkey;

        Sampled += s => Dispatcher.BeginInvoke(() => tray.Update(s));
        alerts.Fired += a => Dispatcher.BeginInvoke(() => ShowAlert(a));

        Listen();
        Task.Run(SampleLoop);
        new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => CheckReminders(), Dispatcher);
        Dispatcher.BeginInvoke(CheckReminders, DispatcherPriority.ApplicationIdle); // catch up right after start
        if (Settings.ExplorerMenu) Settings.ExplorerMenu = true; // re-point the menu at this exe, in case it moved

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
                _ = Dispatcher.BeginInvoke(() => { if (CliArg(args) is { } dir) OpenCli(dir); else ShowMain(); });
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

    void OpenCli(string dir)
    {
        ShowMain();
        ((CliPage)main!.ShowTab("CLI")).Open(dir);
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

    public static void ShowAlert(Alert a)
    {
        Db.Warn($"alert {a.Label} {a.Value:0}{a.Unit} (limit {a.Limit:0})");
        var color = a.Critical ? Color.FromRgb(0xFF, 0x4D, 0x5E) : Color.FromRgb(0xFF, 0x6A, 0x3D);
        AlertWindow.Show(new Toast(a.Label, a.Value.ToString("0"), a.Unit, "Limit", $"{(a.Above ? "≥" : "≤")} {a.Limit:0}{a.Unit}", color, false),
            ((App)Current).ShowMain);
    }

    // Checklist reminders: polled, so ones that came due while Glance was off fire right after start.
    void CheckReminders()
    {
        foreach (var (note, item) in NoteStore.DueReminders(DateTime.Now).ToList())
        {
            item.Reminded = true;
            NoteStore.SaveSoon();
            Db.Info($"reminder {note.DisplayTitle}: {item.Text}");
            AlertWindow.Show(new Toast($"Reminder · {note.DisplayTitle}", item.Text.Length > 0 ? item.Text : "(no text)", "",
                "Due", item.Due!.Value.ToString("HH:mm"), Color.FromRgb(0x5A, 0xA9, 0xFF), true), () =>
            {
                ShowMain();
                main!.ShowTab("Notes");
                NoteStore.RequestFocus(note);
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
                    Db.Metric(s);
                    nextMetric = sw.Elapsed + TimeSpan.FromMinutes(1);
                }
            }
            catch (Exception e) { Db.Error("sample", e); }
            await wake.WaitAsync(isLive ? Math.Max(250, Settings.RefreshMs) : 5000);
        }
    }

    void ShowMain()
    {
        main!.Show();
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
        if (main!.IsVisible && (!requireFocus || Native.GetForegroundWindow() == hwnd)) main.Hide(); else ShowMain();
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

    void Quit()
    {
        if (CliPage.Running > 0 && MessageBox.Show(
                $"{CliPage.Running} terminal{(CliPage.Running == 1 ? " is" : "s are")} open. Exiting stops them and anything running in them.",
                "Glance", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        Db.Info("exit");
        if (saveTimer?.IsEnabled == true) Settings.Save();
        tray!.Hide();
        mutex?.ReleaseMutex();
        Shutdown(); // pages flush their own unsaved state on Application.Exit
    }
}
