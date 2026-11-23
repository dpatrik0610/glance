using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Text.RegularExpressions;

namespace Glance.Services;

/// Serious: something is breaking or about to (failing drive, crash of Windows). Problem: worth a look.
/// Minor: logged as an error but usually harmless, listed last and left off the timeline.
public enum Tier { Minor, Problem, Serious }

/// One kind of trouble Windows logged, grouped: plain-language meaning and advice, when it happened, and the latest event's own text.
public record Report(string Title, string Summary, Tier Tier, string Category, string Meaning, string Advice,
    string Source, int Id, string Log, List<DateTime> Times, string Message, string? Task = null)
{
    public int Count => Times.Count;
    public DateTime Last => Times.Max();
    public bool Serious => Tier == Tier.Serious;
    public string CountText => Count > 1 ? $"×{Count}" : "";
    public string LastText => When(Last);
    public string TierText => Tier switch { Tier.Serious => "Serious", Tier.Problem => "Worth a look", _ => "Usually harmless" };
    public string SourceText => $"{Category} · {Source}, event {Id}{(Task is null ? "" : $" ({Task})")} · {Log} log";
    public string CountSummary => Count == 1 ? $"Once, {When(Last)}" : $"{Count} times · first {When(Times.Min())} · last {When(Last)}";
    public IEnumerable<string> Recent => Times.OrderDescending().Take(12).Select(When);
    public string SearchUrl => "https://www.bing.com/search?q=" + Uri.EscapeDataString($"Windows event {Id} {Source}");

    public static string When(DateTime t) => t.ToString(t.Date == DateTime.Today ? "'today' HH:mm" : t.Year == DateTime.Today.Year ? "MMM d HH:mm" : "MMM d yyyy");

    public override string ToString() => $"{Title}\n{SourceText}\n{CountSummary}\n\nWhat it means: {Meaning}\nWhat to do: {Advice}\n\nWindows' text:\n{Message}";
}

/// What Windows itself recorded that makes a PC slow or unstable. Reads every error and critical event of the System and
/// Application logs (plus warnings from the parts that cause freezes), so kinds of trouble nobody listed here still show up:
/// known events get specific words, the rest are explained by what part of Windows logged them. No admin needed.
public static partial class WinReports
{
    // Warnings that matter: storage stalls, driver resets, low memory, CPU throttling.
    static readonly string[] WarnProviders =
    [
        "disk", "stornvme", "storahci", "storport", "Ntfs", "Display", "Microsoft-Windows-Resource-Exhaustion-Detector",
        "Microsoft-Windows-WHEA-Logger", "Microsoft-Windows-Kernel-Processor-Power",
    ];

    // Repeats of one kind within this long are one incident (a crash logs several events; a bad block retries in bursts).
    static readonly TimeSpan Incident = TimeSpan.FromSeconds(10);

    public static List<Report> Read(TimeSpan range)
    {
        var ms = (long)range.TotalMilliseconds;
        var when = $"TimeCreated[timediff(@SystemTime) &lt;= {ms}]";
        var warn = string.Join(" or ", WarnProviders.Select(p => $"@Name='{p}'"));
        var xml = "<QueryList><Query Id=\"0\">"
            + $"<Select Path=\"System\">*[System[(Level=1 or Level=2) and {when}]]</Select>"
            + $"<Select Path=\"Application\">*[System[(Level=1 or Level=2) and {when}]]</Select>"
            + $"<Select Path=\"System\">*[System[Provider[{warn}] and Level=3 and {when}]]</Select>"
            // Windows' own problem reports (informational): how GPU hangs and other recovered driver stalls show up.
            + $"<Select Path=\"Application\">*[System[Provider[@Name='Windows Error Reporting'] and EventID=1001 and {when}]]</Select>"
            + "</Query></QueryList>";
        var query = new EventLogQuery(null, PathType.LogName, xml) { ReverseDirection = true }; // newest first: a group's text is its latest event's

        var groups = new Dictionary<string, Report>();
        // Windows Error Reporting logs a queued report again on every send attempt (20 reports were 3,000 events here),
        // so each report counts once: report id → its group and slot in Times (null: not counted).
        var reportsSeen = new Dictionary<string, (Report? Group, int Slot)>();
        var since = DateTime.Now - range;
        using var reader = new EventLogReader(query);
        for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
            using (e)
            {
                var known = Known(e);
                if (known == Skip) continue;
                var at = e.TimeCreated ?? DateTime.MinValue;
                string? reportId = null;
                if (e.ProviderName == "Windows Error Reporting" && Prop(e, 19) is { Length: 36 } id)
                {
                    var dumped = DumpTime(e); // when it happened, not when it was last sent
                    if (reportsSeen.TryGetValue(id, out var seen))
                    {
                        // Read newest first, so an earlier send attempt is closer to when the report was made.
                        if (dumped is null && seen.Group is { } sg && at < sg.Times[seen.Slot]) sg.Times[seen.Slot] = at;
                        continue;
                    }
                    reportId = id;
                    at = dumped ?? at;
                    if (at < since) { reportsSeen[id] = (null, -1); continue; } // happened before the range, sent during it
                }
                var key = known?.Title ?? $"{e.ProviderName}|{e.Id}";
                if (groups.TryGetValue(key, out var g))
                {
                    var added = g.Times.TrueForAll(t => (t - at).Duration() >= Incident);
                    if (added) g.Times.Add(at);
                    if (reportId is not null) reportsSeen[reportId] = added ? (g, g.Times.Count - 1) : (null, -1);
                    continue;
                }
                string message;
                try { message = e.FormatDescription() ?? ""; }
                catch (EventLogException) { message = ""; } // the provider's message file is gone
                message = message.Trim();
                if (message.Length > 4000) message = message[..4000] + "…";
                var log = e.LogName ?? "";
                var c = Classify(e.ProviderName ?? "", log);
                var tier = known?.Tier ?? (e.Level == 1 ? Tier.Serious : c.Tier);
                var task = Task(e);
                var title = known?.Title ?? (FirstSentence(message) is { Length: > 0 } s ? Shorten(s, 90)
                    : task is not null ? $"{e.ProviderName}: {task}" : $"{e.ProviderName} event {e.Id}");
                groups[key] = new(title, known?.Summary ?? c.Summary, tier, known?.Category ?? c.Name, known?.Meaning ?? c.Meaning, known?.Advice ?? c.Advice,
                    e.ProviderName ?? "?", e.Id, log, [at], message == "" ? "(Windows has no text for this event.)" : message, task);
                if (reportId is not null) reportsSeen[reportId] = (groups[key], 0);
            }
        return groups.Values.OrderByDescending(r => r.Tier).ThenByDescending(r => r.Last).ToList();
    }

    record Entry(string Title, string Summary, Tier Tier, string? Meaning = null, string? Advice = null, string? Category = null);

    // Events that repeat what another one already says (a crash's problem report, the "previous shutdown was unexpected" note).
    static readonly Entry Skip = new("", "", Tier.Minor);

    // The provider's own name for what the event is about ("Logging/Recovery"), when it has one.
    static string? Task(EventRecord e)
    {
        try { return e.TaskDisplayName is { Length: > 0 } t && t != "None" && !t.All(char.IsDigit) ? t : null; }
        catch (EventLogException) { return null; } // no metadata for this provider
    }

    // Specific words for events worth them. Meaning/Advice left null fall back to the category's.
    static Entry? Known(EventRecord e) => (e.ProviderName, e.Id) switch
    {
        ("Application Error", 1000) => new($"{Prop(e, 0)} crashed", "The app closed unexpectedly", Tier.Problem,
            "The app hit an error it couldn't recover from and Windows closed it. Unsaved work in it was lost.",
            "Once in a while is normal. If it keeps happening, update or reinstall the app; if many different apps crash, suspect memory or drivers."),
        (".NET Runtime", 1026) => new($"{DotNetApp(e)} crashed", "The app closed unexpectedly", Tier.Problem,
            "The app (built on .NET) hit an error it didn't handle and closed. Windows' text below has the technical details for its developer.",
            "Update the app. If it keeps happening, report it to its developer with the text below."),
        ("Application Error", 1005) => new("An app closed: its files couldn't be read", "Usually a disk or drive problem", Tier.Problem,
            "Windows couldn't read a file the app needed (its drive was slow, disconnected or failing), so it closed the app.",
            "Check the drive the app runs from. If that drive also shows errors here, back it up."),
        ("Application Hang", 1002) => new($"{Prop(e, 0)} froze", "Stopped responding and was closed", Tier.Problem,
            "The app stopped responding to Windows (the 'Not responding' state) and was closed, by you or by Windows.",
            "Occasional freezes happen under heavy load. Frequent ones: update the app, and check the drive and memory usage at that time on the chart."),
        ("Microsoft-Windows-Kernel-Power", 41) => new("Unexpected shutdown", "Windows restarted without shutting down", Tier.Serious,
            "The PC turned off or restarted without Windows shutting down first: a hard freeze, a crash, a power cut, or holding the power button.",
            "If you didn't cut the power yourself, look for a blue screen, overheating or a failing power supply around the same time."),
        ("Microsoft-Windows-WER-SystemErrorReporting", 1001) => new("Blue screen", "Windows crashed and restarted", Tier.Serious,
            "Windows itself crashed (a blue screen), almost always because of a driver or faulty hardware.",
            "Update graphics, chipset and storage drivers. If it repeats, test the memory (Windows Memory Diagnostic)."),
        ("Microsoft-Windows-Resource-Exhaustion-Detector", _) => new("Ran low on memory", "Apps slowed down or were closed", Tier.Serious,
            "Windows ran out of memory (RAM plus page file). Everything slows down and apps can be closed to free memory.",
            "Close the heavy apps in Top consumers · Memory, or let Windows manage the page file size."),
        ("Display", 4101) => new("Graphics driver reset", "A freeze or black screen, then recovered", Tier.Problem,
            "The graphics driver stopped responding and Windows restarted it: a short freeze or black screen, sometimes a game crash.",
            "Update or cleanly reinstall the graphics driver. If it happens under load, check GPU temperature and any overclock."),
        ("disk", 7) => new($"{Disk(e)}: bad sectors", "The drive has damaged areas", Tier.Serious,
            "Parts of this drive can no longer be read reliably. Each failed read can freeze the PC for seconds, and damage usually spreads.",
            "Copy anything important off this drive now, then check its health (CrystalDiskInfo) and plan to replace it."),
        ("disk", 51) => new($"{Disk(e)}: paging error", "Swapping memory to disk failed", Tier.Serious,
            "Windows couldn't read or write memory it had moved to this drive: freezes and possible crashes, often a failing drive or cable.",
            "Back up the drive, reseat or replace its cable, and check its health."),
        ("disk", 153) => new($"{Disk(e)}: reads retried", "The drive needed several tries", Tier.Serious,
            "Windows had to retry reading or writing this drive. Each retry stalls whatever was waiting on it: a classic cause of short freezes.",
            "Check the drive's health and cable. If it also shows bad sectors, back it up and replace it."),
        ("disk", 154) => new($"{Disk(e)}: hardware error", "The drive reported a failure", Tier.Serious,
            "The drive itself reported a hardware error while reading or writing.",
            "Back up the drive and check its health; replace it if errors continue."),
        ("stornvme" or "storahci" or "storport", 129) => new("Storage controller reset", "A drive stopped answering", Tier.Serious,
            "A drive didn't answer in time and Windows reset its connection. The whole PC can hang for several seconds while that happens.",
            "Update storage/chipset drivers and check the drive's cable and health."),
        ("Ntfs" or "Microsoft-Windows-Ntfs", 55 or 98) => new("File system corruption", "A drive's file system is damaged", Tier.Serious,
            "Windows found damage in how files are organized on a drive. Files can become unreadable, and it often follows disk errors.",
            "Back up the drive, then run a disk check: chkdsk X: /f in an admin terminal (X = the drive named below)."),
        ("Volsnap", 36) => new("Restore points were dropped", "Shadow copy storage ran out", Tier.Problem,
            "Windows deleted restore points or backup snapshots because the space reserved for them ran out.",
            "If you rely on System Restore or File History, give them more space in System Protection settings."),
        ("disk", 11) => new($"{Disk(e)}: controller error", "The drive's connection failed", Tier.Serious,
            "The controller this drive hangs off reported an error talking to it: a loose or bad cable, a bad port, or a failing drive.",
            "Reseat or swap the drive's data cable and port, then check the drive's health."),
        ("disk", 15) => new($"{Disk(e)}: not ready", "The drive wasn't ready to use", Tier.Problem,
            "Windows tried to use this drive while it wasn't ready, for example still spinning up from sleep or briefly disconnected.",
            "If it repeats, check the cable and power to the drive, and turn off disk sleep in power settings."),
        ("disk", 52) => new($"{Disk(e)}: predicted to fail", "The drive's self-test warns of failure", Tier.Serious,
            "The drive's own health monitoring (SMART) predicts it will fail soon.",
            "Back up everything on this drive now and replace it."),
        ("Ntfs" or "Microsoft-Windows-Ntfs", 50 or 57 or 140) => new("Couldn't save data to a drive", "A write to disk failed", Tier.Serious,
            "Windows couldn't finish writing data to a drive, so recent changes to some files may be lost or damaged. Often a drive that was unplugged, or one that is failing.",
            "Safely remove USB drives before unplugging them. If it's an internal drive, back it up and check its health and cable."),
        ("Ntfs" or "Microsoft-Windows-Ntfs", 137) => new("File system log error", "A drive's change log failed", Tier.Problem,
            "Windows couldn't keep the log it uses to keep a drive's files consistent. It usually follows other disk trouble.",
            "Run a disk check on that drive: chkdsk X: /f in an admin terminal."),
        ("iaStorA" or "iaStorAC" or "iaStorAVC" or "iaStorV" or "iaStorVD", 9) or ("stornvme", 11) => new("Storage controller timeout", "A drive stopped answering", Tier.Serious,
            "A drive didn't answer in time and its controller gave up on the request. The PC can hang for seconds while that happens.",
            "Update the storage (Intel RST / NVMe) and chipset drivers, and check the drive's health and cable."),
        ("nvlddmkm", 13 or 14) => new("NVIDIA driver error", "The graphics driver hit an error", Tier.Problem,
            "NVIDIA's driver reported an error from the graphics card: glitches, a game crash, or a freeze.",
            "Update or cleanly reinstall the NVIDIA driver. If it happens under load, check GPU temperature and undo any overclock."),
        ("nvlddmkm", 153) => new("Graphics driver reset", "A freeze or black screen, then recovered", Tier.Problem,
            "The graphics driver stopped responding and Windows restarted it: a short freeze or black screen, sometimes a game crash.",
            "Update or cleanly reinstall the graphics driver. If it happens under load, check GPU temperature and any overclock."),
        ("Microsoft-Windows-WHEA-Logger", 1 or 18 or 20) => new("Fatal hardware error", "A part failed badly enough to stop Windows", Tier.Serious,
            "The CPU, memory or motherboard reported an error it couldn't correct. Windows usually crashes or restarts right after.",
            "Undo any overclock or XMP/EXPO memory profile, update the BIOS, and test the memory. If it continues, the CPU or board may be at fault."),
        ("Microsoft-Windows-WHEA-Logger", 19) => new("CPU corrected an error", "The processor fixed an internal error", Tier.Problem,
            "The CPU detected and corrected an internal error. Rare ones are harmless; frequent ones mean it's running at its edge.",
            "Undo any CPU overclock or undervolt and update the BIOS."),
        ("Microsoft-Windows-WHEA-Logger", 17) => new("PCIe connection error", "A card or NVMe drive's link had errors", Tier.Problem,
            "A PCI Express device (graphics card, NVMe drive, network card) had errors on its connection, which were corrected. They can cause stutters.",
            "Reseat the card, update the BIOS and chipset drivers. Turning off PCIe power saving (Link State Power Management) often stops it."),
        ("Microsoft-Windows-WHEA-Logger", 46 or 47) => new("Memory corrected an error", "RAM had a fixable error", Tier.Problem,
            "The memory reported an error that was corrected. Repeated ones point at unstable memory settings or a failing module.",
            "Turn off the XMP/EXPO profile to test, then run Windows Memory Diagnostic."),
        ("Microsoft-Windows-WHEA-Logger", _) => new(e.Level <= 2 ? "Hardware error" : "Hardware warning", "A part reported an error", e.Level <= 2 ? Tier.Serious : Tier.Problem,
            "A hardware part reported an error. Corrected errors are survivable but frequent ones point at an unstable overclock, memory or the CPU.",
            "Undo any overclock or XMP/EXPO profile to test, update the BIOS, and test the memory."),
        ("Microsoft-Windows-MemoryDiagnostics-Results", 1102 or 1202) => new("Memory test found errors", "Windows Memory Diagnostic failed", Tier.Serious,
            "Windows' memory test found errors in the RAM. That causes random crashes and corrupted files.",
            "Turn off XMP/EXPO and test again; if errors remain, test the modules one at a time and replace the bad one."),
        ("Service Control Manager", 7043) => new($"{Prop(e, 0)} slowed shutdown", "A service didn't stop in time", Tier.Problem,
            "A background service didn't shut down when asked, so Windows waited for it: shutdown and restart take longer.",
            "Update or remove the app the service belongs to."),
        ("Service Control Manager", 7026) => new("A boot driver didn't load", "A driver failed at startup", Tier.Problem,
            "A driver meant to load at startup failed. Usually left over from uninstalled software; sometimes a device stops working.",
            "Check whether a device stopped working; otherwise reinstall or remove the software the driver belongs to (named below)."),
        ("Microsoft-Windows-Kernel-PnP", 219) => new("A device driver didn't load", "A device may not work", Tier.Problem,
            "Windows couldn't load the driver for a device. The device named below may not work, and startup can be slower while Windows waits for it.",
            "Update or reinstall that device's driver from its maker, or unplug the device if you don't use it."),
        ("Tcpip", 4199) => new("IP address conflict", "Another device has the same address", Tier.Problem,
            "Another device on the network uses the same IP address as this PC, so the connection drops or misbehaves.",
            "Restart the router, or give this PC (or the other device) a different address."),
        ({ } p, 5002 or 6062) when p.StartsWith("Netwtw") => new("Wi-Fi adapter reset", "The Wi-Fi card restarted", Tier.Problem,
            "The Intel Wi-Fi card stopped working and its driver restarted it: a short disconnect, lag spikes in games.",
            "Update the Intel Wi-Fi driver from Intel, and turn off power saving for the adapter in Device Manager."),
        ("MsiInstaller", 11708) => new("An install failed", "An app couldn't be installed", Tier.Minor,
            "An app's installer (named below) failed and rolled back.", "Run the installer again, as administrator if it asks."),
        ("SideBySide", 33) => new("An app couldn't start", "Missing app components", Tier.Problem,
            "An app couldn't start because components it needs (usually a Microsoft Visual C++ runtime) are missing or mismatched.",
            "Install the latest Microsoft Visual C++ Redistributables (x64 and x86), or reinstall the app."),
        ("Windows Error Reporting", 1001) => Wer(e),
        ("EventLog", 6008) => Skip, // "previous shutdown was unexpected": Kernel-Power 41 says it already
        ("Microsoft-Windows-Kernel-Processor-Power", 37) => new("CPU slowed by firmware", "Held below full speed", Tier.Problem,
            "The motherboard limited the CPU's speed, usually because of heat or power limits. Everything runs slower meanwhile.",
            "Check CPU temperature and cooling (dust, fans, thermal paste) and power settings."),
        ("Service Control Manager", 7000 or 7009 or 7011) => new($"{Prop(e, 0)} didn't start", "A Windows service failed to start", Tier.Problem,
            "A background service failed or took too long to start. Slow-starting services can also make startup slow.",
            "If you uninstalled the app the service belongs to, ignore it. Otherwise reinstall or update that app."),
        ("Service Control Manager", 7023 or 7024 or 7031 or 7034) => new($"{Prop(e, 0)} stopped unexpectedly", "A Windows service crashed or failed", Tier.Problem,
            "A background service stopped with an error. Whatever depends on it may misbehave until it restarts.",
            "Once is usually fine. If it repeats, update the app or driver the service belongs to."),
        ("Microsoft-Windows-WindowsUpdateClient", 20) => new("A Windows update failed", "An update didn't install", Tier.Problem,
            "An update failed to install. Windows usually retries on its own.",
            "Open Settings › Windows Update and retry; if the same one keeps failing, search its error code."),
        ("Microsoft-Windows-DistributedCOM", _) => new("A background component didn't start", "Usually harmless", Tier.Minor),
        ("Microsoft-Windows-Windows Firewall With Advanced Security", 2042) => new("Firewall couldn't read a setting", "Usually harmless", Tier.Minor),
        ("Microsoft-Windows-Perflib", _) => new("Performance counters didn't load", "Usually harmless", Tier.Minor,
            "An app's performance counters (what monitoring tools read) couldn't load. It doesn't slow anything down.", "Nothing to do."),
        _ => null,
    };

    // Windows Error Reporting's summary of a problem report: its kind ("LiveKernelEvent", "APPCRASH"…) and parameters P1….
    // Crashes and blue screens are already in the log as their own events, so those reports are skipped.
    static Entry Wer(EventRecord e)
    {
        var (kind, p1) = (Prop(e, 2), Prop(e, 5));
        if (kind is "APPCRASH" or "CLR20r3" or "BlueScreen" or "BEX" or "BEX64" or "MoBEX" || kind.StartsWith("AppHang") || kind.StartsWith("MoApp")) return Skip;
        if (kind == "LiveKernelEvent") return LiveKernel(p1.ToLowerInvariant());
        if (kind == "InPageError") return new("Couldn't read data from a drive", "Windows failed to load data it needed from disk", Tier.Serious,
            "Windows needed data from a drive (part of a program, or memory it had moved to disk) and the read failed. The app usually crashes, and it points at a failing drive or cable.",
            "Check the drives with errors in this list; back up any that show bad sectors or retried reads.", "Storage");
        if (kind.StartsWith("RADAR_PRE_LEAK")) return new($"{p1} kept using more memory", "Windows detected a likely memory leak", Tier.Minor,
            "Windows noticed this app's memory use growing and growing, the sign of a memory leak. Left running, it slows the PC down.",
            "Restart the app now and then, and update it.", "Problem reports");
        return new($"Problem report: {Words(kind)}", "Filed by Windows Error Reporting", Tier.Minor,
            "Windows filed a problem report of this kind (they go to Microsoft to find common bugs). On its own it rarely means anything is wrong with this PC.",
            "Nothing to do unless something visibly failed at that time.", "Problem reports");
    }

    // A driver or device hung and Windows recovered, saving a diagnostic snapshot: what Reliability Monitor calls "Hardware error".
    static Entry LiveKernel(string code) => code switch
    {
        "141" or "117" or "193" => new("Graphics card stopped responding", "Windows reset it and recovered", Tier.Problem,
            "The graphics card or its driver hung and Windows reset it. You'd have seen a freeze, a black screen or a game crash at that moment.",
            "Update or cleanly reinstall the graphics driver, undo any GPU overclock, and check GPU temperature.", "Graphics"),
        "124" => new("Hardware error captured", "The hardware reported an error; Windows kept running", Tier.Serious,
            "The CPU, memory or a bus reported a hardware error and Windows saved a snapshot of it without crashing.",
            "Undo any overclock or XMP/EXPO profile, update the BIOS, and test the memory.", "Hardware"),
        "144" => new("USB controller stopped responding", "A USB device or hub hung", Tier.Problem,
            "A USB controller or something plugged into it (hub, dock, headset…) hung and Windows recovered it.",
            "Update chipset/USB drivers and the dock's firmware; try the device in another port.", "Hardware"),
        "1a1" => new("Windows' interface froze", "The desktop stopped responding for a while", Tier.Problem,
            "The part of Windows that draws windows stopped responding long enough for Windows to capture a diagnostic snapshot.",
            "Often caused by a graphics driver or a heavily stuck app: update the graphics driver and check the chart for load at that time.", "Startup & power"),
        "ab" => new("An app left memory behind at sign-out", "Usually harmless", Tier.Minor,
            "A driver or app didn't release all of its memory when you signed out.", "Nothing to do.", "Hardware"),
        _ => new($"A driver stopped responding (code {code})", "Windows recovered and saved a snapshot", Tier.Problem,
            "A driver or device hung and Windows recovered without a blue screen, saving a diagnostic snapshot. You may have seen a freeze.",
            "Search the code online (button below) to see which kind of driver it is, then update that driver.", "Hardware"),
    };

    // The snapshot a problem report attached ("…\WATCHDOG-20261027-1713.dmp") is stamped with when it was taken.
    static DateTime? DumpTime(EventRecord e) => DumpStamp().Match(Prop(e, 15)) is { Success: true } m
        && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd-HHmm", null, System.Globalization.DateTimeStyles.None, out var t) ? t : null;

    [GeneratedRegex(@"-(\d{8}-\d{4})\.dmp")]
    private static partial Regex DumpStamp();

    // "StoreAgentInstallFailure1" → "Store agent install failure"
    static string Words(string name)
    {
        var s = Regex.Replace(name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'), "(?<=[a-z])(?=[A-Z])", " ").Replace('_', ' ').Trim();
        return s.Length > 1 ? char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant() : name;
    }

    /// Reliability Monitor's stability index (1–10, Windows' own hourly score from failures and warnings), oldest first,
    /// starting a day before the range so the line has a value at its left edge. Empty when Windows has it turned off.
    public static List<(DateTime At, float Index)> Stability(TimeSpan range)
    {
        var since = ManagementDateTimeConverter.ToDmtfDateTime(DateTime.Now - range - TimeSpan.FromDays(1));
        using var search = new ManagementObjectSearcher(
            $"SELECT SystemStabilityIndex, TimeGenerated FROM Win32_ReliabilityStabilityMetrics WHERE TimeGenerated >= '{since}'");
        var list = new List<(DateTime, float)>();
        foreach (var o in search.Get())
            using (o)
                list.Add((ManagementDateTimeConverter.ToDateTime((string)o["TimeGenerated"]), Convert.ToSingle(o["SystemStabilityIndex"])));
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    record Kind(string Name, string Summary, string Meaning, string Advice, Tier Tier);

    // Every event lands in one of these by which part of Windows logged it, so new kinds of events still get plain words.
    static Kind Classify(string provider, string log)
    {
        bool Has(params string[] parts) => parts.Any(p => provider.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (Has("disk", "stor", "nvme", "iaStor", "Ntfs", "volmgr", "Volsnap", "partmgr", "chkdsk", "fastfat", "exfat"))
            return new("Storage", "A drive or file system problem",
                "A drive or its file system had trouble reading or writing. Storage trouble causes freezes and can mean a drive is failing.",
                "Back up what matters on that drive, check its health (CrystalDiskInfo), and its cable.", Tier.Problem);
        if (Has("Display", "nvlddmkm", "amdkmdag", "amdwddmg", "igfx", "Dxg", "Graphics"))
            return new("Graphics", "A graphics driver problem",
                "The graphics driver reported a problem: possible stutters, black screens or game crashes.",
                "Update or cleanly reinstall the graphics driver; check GPU temperature and any overclock.", Tier.Problem);
        if (Has("WHEA", "HAL", "ACPI", "Processor", "Kernel-PnP", "USB", "Bluetooth"))
            return new("Hardware", "A device or firmware problem",
                "A piece of hardware, its firmware or its driver reported a problem.",
                "Update the BIOS and the device's driver. Unplug devices you don't need to see if it stops.", Tier.Problem);
        if (Has("Kernel-Power", "Kernel-Boot", "Kernel-General", "WinRE", "Wininit", "Winlogon", "BugCheck"))
            return new("Startup & power", "A startup, shutdown or power problem",
                "Windows had trouble starting, shutting down or with power. Repeated, it points at drivers or the power supply.",
                "Install pending updates and drivers; if the PC shuts off by itself, check temperatures and the power supply.", Tier.Problem);
        if (Has("Service Control Manager"))
            return new("Services", "A background service problem",
                "A background service (part of Windows or of an installed app) failed or stopped.",
                "Reinstall or update the app it belongs to, or ignore it if that app is gone.", Tier.Problem);
        if (Has("WindowsUpdate", "Servicing", "CBS", "Store", "AppX", "AppModel"))
            return new("Updates & apps", "An update or app install problem",
                "Installing or updating Windows or a Store app failed.",
                "Retry from Settings › Windows Update or the Store; Windows usually fixes it on a later try.", Tier.Problem);
        if (Has("Tcpip", "DNS", "Dhcp", "NetBT", "NDIS", "WLAN", "Netwtw", "e1d", "rt6", "Network", "Wcm", "Iphlpsvc"))
            return new("Network", "A network problem",
                "The network connection or a network driver had a problem: drops, slow loading or lag in online games.",
                "Update the network driver, restart the router, and check the cable or Wi-Fi signal.", Tier.Problem);
        if (Has("Schannel", "Firewall", "Defender", "Security", "Kerberos", "LSA", "Audit", "CertificateServices"))
            return new("Security", "A security component problem",
                "A security part of Windows (encryption, firewall, antivirus, sign-in) logged an error. Most of these are harmless.",
                "If something stopped working, check Windows Security; otherwise nothing to do.", Tier.Minor);
        if (Has("Application Error", "Application Hang", ".NET Runtime", "Windows Error Reporting", "SideBySide", "MsiInstaller"))
            return new("Apps", "An app problem",
                "An app crashed, froze or failed to start or install.",
                "Update or reinstall the app; if many apps fail, suspect drivers or memory.", Tier.Problem);
        if (Has("DistributedCOM", "Perflib", "User Profiles", "Winmgmt", "WMI", "EventSystem", "Time-Service"))
            return new("Background", "Background noise",
                "A background part of Windows logged an error that rarely affects anything you'd notice.",
                "Nothing to do unless something is visibly broken.", Tier.Minor);
        // An app logging its own errors into the Application log: its business, rarely the PC's.
        return log == "Application"
            ? new("Apps", "An app logged an error",
                $"{provider} logged an error of its own. It concerns that app, not Windows.",
                "If that app misbehaves, update it or check its own logs; otherwise ignore it.", Tier.Minor)
            : new("Windows", "A Windows component problem",
                $"The Windows component \"{provider}\" logged an error. Windows' own text below says what it was.",
                "Search the event online (button below) for what it means on other PCs.", Tier.Problem);
    }

    static string FirstSentence(string message)
    {
        var line = message.Split('\n')[0].Trim();
        var dot = line.IndexOf(". ", StringComparison.Ordinal);
        return (dot > 0 ? line[..(dot + 1)] : line).TrimEnd('.');
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    static string Prop(EventRecord e, int i) => e.Properties.Count > i ? e.Properties[i].Value?.ToString() ?? "?" : "?";

    // "\Device\Harddisk0\DR0" → "Disk 0", the number Disk Management shows.
    static string Disk(EventRecord e) => HarddiskNumber().Match(Prop(e, 0)) is { Success: true } m ? "Disk " + m.Groups[1].Value : "A disk";

    // .NET's crash text starts "Application: name.exe".
    static string DotNetApp(EventRecord e) => DotNetName().Match(Prop(e, 0)) is { Success: true } m ? m.Groups[1].Value : "A .NET app";

    [GeneratedRegex(@"Harddisk(\d+)")]
    private static partial Regex HarddiskNumber();

    [GeneratedRegex(@"Application: (\S+)")]
    private static partial Regex DotNetName();
}
