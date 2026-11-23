using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace Glance.Services;

/// One kind of trouble Windows logged, grouped: how often, when last, and the latest event's own text.
public record Report(string Title, string Detail, List<DateTime> Times, bool Serious, string Message)
{
    public int Count => Times.Count;
    public DateTime Last => Times.Max();
    public string CountText => Count > 1 ? $"×{Count}" : "";
    public string LastText => Last.ToString(Last.Date == DateTime.Today ? "'today' HH:mm" : "MMM d HH:mm");
}

/// What Windows itself recorded that makes a PC slow or unstable: app crashes and freezes, disk and storage errors,
/// driver resets, blue screens, low memory. Read from the event logs (no admin needed), warnings and worse only.
public static partial class WinReports
{
    static readonly string[] AppProviders = ["Application Error", "Application Hang"];
    static readonly string[] SystemProviders =
    [
        "disk", "stornvme", "storahci", "storport", "Ntfs", "Display", "Microsoft-Windows-Kernel-Power",
        "Microsoft-Windows-WER-SystemErrorReporting", "Microsoft-Windows-Resource-Exhaustion-Detector",
        "Microsoft-Windows-WHEA-Logger", "Microsoft-Windows-Kernel-Processor-Power",
    ];

    public static List<Report> Read(TimeSpan range)
    {
        var ms = (long)range.TotalMilliseconds;
        string Select(string log, string[] providers) =>
            $"<Select Path=\"{log}\">*[System[Provider[{string.Join(" or ", providers.Select(p => $"@Name='{p}'"))}]"
            + $" and (Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) &lt;= {ms}]]]</Select>";
        var query = new EventLogQuery(null, PathType.LogName,
            $"<QueryList><Query Id=\"0\">{Select("Application", AppProviders)}{Select("System", SystemProviders)}</Query></QueryList>")
            { ReverseDirection = true }; // newest first, so a group's message is its latest event's

        var groups = new Dictionary<string, Report>();
        using var reader = new EventLogReader(query);
        for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
            using (e)
            {
                var at = e.TimeCreated ?? DateTime.MinValue;
                var (title, detail, serious) = Describe(e);
                if (groups.TryGetValue(title, out var g))
                {
                    g.Times.Add(at);
                    continue;
                }
                string message;
                try { message = e.FormatDescription() ?? ""; }
                catch (EventLogException) { message = ""; } // the provider's message file is gone
                if (message.Length > 600) message = message[..600] + "…";
                groups[title] = new(title, detail ?? FirstSentence(message), [at], serious, message);
            }
        return groups.Values.OrderByDescending(r => r.Serious).ThenByDescending(r => r.Last).ToList();
    }

    // Plain words for the events that matter; anything else these providers log keeps Windows' own first line (detail null).
    static (string Title, string? Detail, bool Serious) Describe(EventRecord e) => (e.ProviderName, e.Id) switch
    {
        ("Application Error", 1000) => ($"{Prop(e, 0)} crashed", "The app closed unexpectedly", false),
        ("Application Hang", 1002) => ($"{Prop(e, 0)} froze", "Stopped responding and was closed", false),
        ("Microsoft-Windows-Kernel-Power", 41) => ("Unexpected shutdown", "Windows restarted without shutting down: a hard freeze, crash or power loss", true),
        ("Microsoft-Windows-WER-SystemErrorReporting", 1001) => ("Blue screen", "Windows crashed and restarted", true),
        ("Microsoft-Windows-Resource-Exhaustion-Detector", _) => ("Ran low on memory", "Apps slowed down or were closed to free memory", true),
        ("Display", 4101) => ("Graphics driver reset", "The display driver stopped responding and recovered: a freeze or black screen", false),
        ("disk", 7) => ($"{Disk(e)}: bad sectors", "The drive has damaged areas. Back it up and check its health", true),
        ("disk", 51) => ($"{Disk(e)}: paging error", "Reading or writing the page file failed: freezes, possibly a failing drive", true),
        ("disk", 153) => ($"{Disk(e)}: reads retried", "The drive needed several tries: freezes, a failing drive or loose cable", true),
        ("disk", 154) => ($"{Disk(e)}: hardware error", "The drive reported a hardware failure", true),
        ("stornvme" or "storahci" or "storport", 129) => ("Storage controller reset", "A drive stopped answering and was reset: the PC hangs for seconds", true),
        ("Ntfs", 55) => ("File system corruption", "Windows found damage on a drive's file system. Run a disk check (chkdsk)", true),
        ("Microsoft-Windows-WHEA-Logger", _) => ("Hardware error", "The CPU, memory or a bus reported an error", true),
        ("Microsoft-Windows-Kernel-Processor-Power", 37) => ("CPU slowed by firmware", "The CPU was held below full speed, usually for heat or power", false),
        var (p, id) => ($"{p} · event {id}", null, false),
    };

    static string FirstSentence(string message)
    {
        var line = message.Split('\n')[0].Trim();
        var dot = line.IndexOf(". ", StringComparison.Ordinal);
        return dot > 0 ? line[..(dot + 1)] : line;
    }

    static string Prop(EventRecord e, int i) => e.Properties.Count > i ? e.Properties[i].Value?.ToString() ?? "?" : "?";

    // "\Device\Harddisk0\DR0" → "Disk 0", the number Disk Management shows.
    static string Disk(EventRecord e) => HarddiskNumber().Match(Prop(e, 0)) is { Success: true } m ? "Disk " + m.Groups[1].Value : "A disk";

    [GeneratedRegex(@"Harddisk(\d+)")]
    private static partial Regex HarddiskNumber();
}
