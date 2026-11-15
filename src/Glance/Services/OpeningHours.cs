using System.Text.RegularExpressions;

namespace Glance.Services;

/// "Open · closes 18:00" from an OpenStreetMap opening_hours value ("Mo-Fr 08:00-18:00; Sa 09:00-13:00; PH off").
/// Covers the common subset: weekday lists and ranges, several time ranges, past-midnight closing, off, 24/7.
/// Anything else (months, week numbers, sunrise, holidays-only) gives no status rather than a wrong one.
public static partial class OpeningHours
{
    static readonly string[] Codes = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];
    static readonly string[] Names = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// Status line, or null when there are no hours or they're in syntax this doesn't read.
    public static string? Status(string? spec, DateTime now, out bool open)
    {
        open = false;
        if (string.IsNullOrWhiteSpace(spec) || Parse(spec) is not { } week) return null;
        if (spec.Trim() == "24/7") { open = true; return "Open 24/7"; }
        int today = ((int)now.DayOfWeek + 6) % 7; // Monday = 0
        var t = now.TimeOfDay;

        foreach (var (_, to) in week[(today + 6) % 7]) // yesterday's hours running past midnight
            if (to > Day && t < to - Day) { open = true; return $"Open · closes {Hm(to)}"; }
        foreach (var (from, to) in week[today])
            if (t >= from && t < to) { open = true; return $"Open · closes {Hm(to)}"; }

        if (week[today].Where(r => r.From > t).Select(r => r.From).DefaultIfEmpty(Day).Min() is var next && next < Day)
            return $"Closed · opens {Hm(next)}";
        for (var i = 1; i <= 7; i++)
            if (week[(today + i) % 7] is { Count: > 0 } d)
                return $"Closed · opens {(i == 1 ? "tomorrow" : Names[(today + i) % 7])} {Hm(d.Min(r => r.From))}";
        return "Closed";
    }

    /// Open ranges per weekday (Monday first). Later rules replace earlier ones for the days they name, as in OSM.
    public static List<(TimeSpan From, TimeSpan To)>[]? Parse(string spec)
    {
        var week = Enumerable.Range(0, 7).Select(_ => new List<(TimeSpan, TimeSpan)>()).ToArray();
        if (spec.Trim() == "24/7")
        {
            foreach (var d in week) d.Add((TimeSpan.Zero, Day));
            return week;
        }
        foreach (var raw in RuleSplit().Split(spec))
        {
            var rule = raw.Trim();
            if (rule.Length == 0) continue;
            IEnumerable<int> days = Enumerable.Range(0, 7);
            if (DaysPrefix().Match(rule) is { Success: true } m)
            {
                var tokens = m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).Where(x => x != "PH").ToList();
                if (tokens.Count == 0) continue; // a holidays-only rule ("PH off"): we don't know the holidays
                var list = new List<int>();
                foreach (var tok in tokens)
                {
                    var ends = tok.Split('-', StringSplitOptions.TrimEntries);
                    int a = Array.IndexOf(Codes, ends[0]), b = Array.IndexOf(Codes, ends[^1]);
                    if (a < 0 || b < 0) return null; // "PH" inside a range and such
                    for (var i = a; ; i = (i + 1) % 7) { list.Add(i); if (i == b) break; } // Fr-Mo wraps
                }
                days = list;
                rule = m.Groups[2].Value.Trim();
            }
            var times = new List<(TimeSpan, TimeSpan)>();
            if (rule is not ("off" or "closed"))
                foreach (var range in rule.Split(',', StringSplitOptions.TrimEntries))
                {
                    if (TimeRange().Match(range) is not { Success: true } r) return null;
                    var from = new TimeSpan(int.Parse(r.Groups[1].Value), int.Parse(r.Groups[2].Value), 0);
                    var to = new TimeSpan(int.Parse(r.Groups[3].Value), int.Parse(r.Groups[4].Value), 0);
                    if (from >= Day || to > Day) return null;
                    times.Add((from, to <= from ? to + Day : to)); // 22:00-02:00 runs into the next day
                }
            foreach (var d in days) week[d] = [.. times];
        }
        return week;
    }

    static string Hm(TimeSpan t) => $"{(int)t.TotalHours % 24:00}:{t.Minutes:00}";

    // Rules end at ";", or at a "," that follows a time (or off) and starts a new day list: "Mo-Fr 09:00-17:00, Sa 10:00-12:00".
    [GeneratedRegex(@";|(?<=\d|off|closed)\s*,\s*(?=(?:Mo|Tu|We|Th|Fr|Sa|Su|PH)\b)")]
    private static partial Regex RuleSplit();

    [GeneratedRegex(@"^((?:Mo|Tu|We|Th|Fr|Sa|Su|PH)(?:\s*[-,]\s*(?:Mo|Tu|We|Th|Fr|Sa|Su|PH))*)\b\s*(.*)$")]
    private static partial Regex DaysPrefix();

    [GeneratedRegex(@"^(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})$")]
    private static partial Regex TimeRange();
}
