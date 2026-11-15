using System.Globalization;
using System.Text.RegularExpressions;

namespace Glance.Services;

/// Typed due dates: "tomorrow 9", "fri 14:00", "in 30m", "18:00", "11.03", "2026-11-03 8:15", "Nov 3 14:00".
/// Format() output parses back to the same moment, so re-committing a shown value never shifts it.
public static partial class Due
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;
    static readonly string[] Weekdays = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    static readonly TimeSpan DefaultTime = TimeSpan.FromHours(9); // date without a time

    [GeneratedRegex(@"^in\s*(\d+)\s*(m|min|mins|minutes?|h|hrs?|hours?|d|days?)$")]
    private static partial Regex Relative();

    [GeneratedRegex(@"^(?:(?<rel>today|tomorrow|tmrw|yesterday)|(?<wd>sun|mon|tue|wed|thu|fri|sat)[a-z]*|(?:(?<my>\d{4})\s+)?(?<mon>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\s+(?<md>\d{1,2})|(?:(?<y>\d{4})[-.])?(?<m>\d{1,2})[-.](?<d>\d{1,2})\.?)?\s*(?:(?<h>\d{1,2})(?::(?<min>\d{2}))?)?$")]
    private static partial Regex Absolute();

    public static DateTime? Parse(string input, DateTime now)
    {
        var s = input.Trim().ToLowerInvariant();
        if (s.Length == 0) return null;

        var r = Relative().Match(s);
        if (r.Success)
        {
            var n = int.Parse(r.Groups[1].Value);
            return r.Groups[2].Value[0] switch { 'm' => now.AddMinutes(n), 'h' => now.AddHours(n), _ => now.AddDays(n) };
        }

        var a = Absolute().Match(s);
        var hasDay = a.Groups["rel"].Success || a.Groups["wd"].Success || a.Groups["mon"].Success || a.Groups["m"].Success;
        if (!a.Success || (!hasDay && !a.Groups["h"].Success)) return null;

        var time = DefaultTime;
        if (a.Groups["h"].Success)
        {
            int h = int.Parse(a.Groups["h"].Value), min = a.Groups["min"].Success ? int.Parse(a.Groups["min"].Value) : 0;
            if (h > 23 || min > 59) return null;
            time = new TimeSpan(h, min, 0);
        }

        DateTime date;
        if (a.Groups["rel"].Success)
            date = now.Date.AddDays(a.Groups["rel"].Value switch { "today" => 0, "yesterday" => -1, _ => 1 });
        else if (a.Groups["wd"].Success)
        {
            var diff = (Array.IndexOf(Weekdays, a.Groups["wd"].Value) - (int)now.DayOfWeek + 7) % 7;
            date = now.Date.AddDays(diff);
            if (diff == 0 && date + time <= now) date = date.AddDays(7); // "mon 9" said on Monday at 10 → next week
        }
        else if (a.Groups["mon"].Success || a.Groups["m"].Success)
        {
            var (yGroup, month, day) = a.Groups["mon"].Success
                ? (a.Groups["my"], Array.IndexOf(Months, a.Groups["mon"].Value) + 1, int.Parse(a.Groups["md"].Value))
                : (a.Groups["y"], int.Parse(a.Groups["m"].Value), int.Parse(a.Groups["d"].Value));
            var year = yGroup.Success ? int.Parse(yGroup.Value) : now.Year;
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
            date = new DateTime(year, month, day);
            if (!yGroup.Success && date + time < now.Date) date = date.AddYears(1); // "01.15" in December → next January
        }
        else
        {
            date = now.Date; // time only: the next time it's that o'clock
            if (date + time <= now) date = date.AddDays(1);
        }
        return date + time;
    }

    public static string Format(DateTime d, DateTime now)
    {
        var days = (d.Date - now.Date).Days;
        var day = days switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            > 1 and < 7 => d.ToString("ddd", En),
            _ => d.ToString(d.Year == now.Year ? "MMM d" : "yyyy MMM d", En),
        };
        return $"{day} {d.ToString("HH:mm", En)}";
    }
}
