using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Json;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace Glance.Services;

/// Selected: ticked in Google Calendar's own list (Glance's default for showing it).
public record CalInfo(string Id, string Name, Color Color, bool Writable, bool Primary, int[] DefaultPopups, bool Selected = true)
{
    public bool Shown => App.Settings.CalendarsShown.TryGetValue(Id, out var on) ? on : Selected;
}

/// One occurrence (repeating events come expanded). All-day: Start and End are dates, End exclusive, as Google has them.
/// Popups: minutes before Start that a reminder pops up.
public record CalEvent(string Id, CalInfo Calendar, string Title, DateTime Start, DateTime End, bool AllDay, string Location,
    string Description, int[] Popups, bool UsesDefaultReminders, bool Recurring)
{
    /// Overlaps that day at all (multi-day and past-midnight events show on each day they touch).
    public bool On(DateTime day) => Start < day.AddDays(1) && (End > day || Start >= day);
}

/// What the event editor hands back. Reminder: null = the calendar's default, -1 = none, else minutes before.
public record EventDraft(string Title, CalInfo Calendar, DateTime Start, DateTime End, bool AllDay, string Location, string Description, int? Reminder);

/// Two-way Google Calendar through the user's own OAuth client (Desktop app type). The sign-in is kept encrypted for this
/// Windows user (DPAPI) in %AppData%\Glance\google, so it survives restarts without asking again.
public static class GoogleCalendar
{
    const string User = "glance";
    static readonly string[] Scopes = [CalendarService.Scope.Calendar];
    static readonly ProtectedStore tokens = new();
    static CalendarService? service;

    public static bool Connected => service is not null;
    public static bool HasClient => App.Settings.GoogleClientId.Trim().Length > 0 && App.Settings.GoogleClientSecret.Trim().Length > 0;

    static ClientSecrets Secrets => new() { ClientId = App.Settings.GoogleClientId.Trim(), ClientSecret = App.Settings.GoogleClientSecret.Trim() };

    static CalendarService Make(UserCredential c) => new(new BaseClientService.Initializer { HttpClientInitializer = c, ApplicationName = "Glance" });

    /// Opens Google's consent page in the browser and waits for the answer (it lands on a localhost port).
    public static async Task Connect(CancellationToken ct)
    {
        var c = await GoogleWebAuthorizationBroker.AuthorizeAsync(Secrets, Scopes, User, ct, tokens);
        service = Make(c);
        Db.Info("google calendar connected");
    }

    /// With a sign-in kept from before, without ever opening the browser. False: none kept (or no client set up).
    public static async Task<bool> Resume()
    {
        if (service is not null) return true;
        if (!HasClient) return false;
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = Secrets, Scopes = Scopes, DataStore = tokens });
        if (await flow.LoadTokenAsync(User, CancellationToken.None) is not { } token) return false;
        service = Make(new UserCredential(flow, User, token));
        return true;
    }

    public static async Task Disconnect()
    {
        service = null;
        upcoming = [];
        await tokens.ClearAsync();
        Db.Info("google calendar disconnected");
    }

    /// A user-facing reason. A sign-in Google no longer accepts (revoked, or a "Testing" app's 7 days ran out) also disconnects.
    public static string Why(Exception e)
    {
        if (e is TokenResponseException)
        {
            service = null;
            _ = tokens.ClearAsync();
            return "Google sign-in expired or was revoked: connect again.";
        }
        return e switch
        {
            GoogleApiException g => $"Google said no: {g.Error?.Message ?? g.Message}",
            HttpRequestException or TaskCanceledException => "Can't reach Google: check the connection.",
            _ => e.Message,
        };
    }

    public static bool IsGoogleError(Exception e) => e is TokenResponseException or GoogleApiException or HttpRequestException or TaskCanceledException;

    public static CalInfo[] Calendars { get; private set; } = [];

    /// Every event of the calendars shown in Glance (CalInfo.Shown), between the two times. Calendars gets all of them,
    /// shared ones included, even those hidden in Google's own list.
    public static async Task<List<CalEvent>> Events(DateTime from, DateTime to)
    {
        var s = service ?? throw new InvalidOperationException("Google Calendar isn't connected.");
        var req = s.CalendarList.List();
        req.ShowHidden = true;
        var list = await req.ExecuteAsync();
        Calendars = (list.Items ?? []).Select(Info).OrderByDescending(c => c.Primary).ThenByDescending(c => c.Writable).ThenBy(c => c.Name).ToArray();
        var found = await Task.WhenAll(Calendars.Where(c => c.Shown).Select(async c =>
        {
            var events = new List<CalEvent>();
            string? page = null;
            do
            {
                var req = s.Events.List(c.Id);
                (req.TimeMinDateTimeOffset, req.TimeMaxDateTimeOffset, req.SingleEvents, req.MaxResults, req.PageToken) = (from, to, true, 2500, page);
                var r = await req.ExecuteAsync();
                events.AddRange((r.Items ?? []).Where(e => e.Status != "cancelled" && e.Start is not null).Select(e => Convert(e, c)));
                page = r.NextPageToken;
            } while (page is not null);
            return events;
        }));
        return found.SelectMany(e => e).OrderBy(e => !e.AllDay).ThenBy(e => e.Start).ToList();
    }

    static CalInfo Info(CalendarListEntry c) => new(c.Id, c.SummaryOverride ?? c.Summary ?? c.Id,
        (Color)ColorConverter.ConvertFromString(c.BackgroundColor ?? "#5AA9FF"), c.AccessRole is "owner" or "writer", c.Primary == true,
        (c.DefaultReminders ?? []).Where(r => r.Method == "popup").Select(r => r.Minutes ?? 0).ToArray(), c.Selected == true && c.Hidden != true);

    static CalEvent Convert(Event e, CalInfo c)
    {
        static DateTime At(EventDateTime t) => t.Date is { } d ? DateTime.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : t.DateTimeDateTimeOffset?.LocalDateTime ?? DateTime.MinValue;
        var allDay = e.Start.Date is not null;
        var byDefault = e.Reminders?.UseDefault != false;
        // The calendar's default popups are for timed events; all-day ones have their own, which the API doesn't give.
        var popups = byDefault ? (allDay ? [] : c.DefaultPopups)
            : (e.Reminders!.Overrides ?? []).Where(r => r.Method == "popup").Select(r => r.Minutes ?? 0).ToArray();
        var start = At(e.Start);
        return new(e.Id, c, string.IsNullOrWhiteSpace(e.Summary) ? "(no title)" : e.Summary, start, e.End is null ? start : At(e.End), allDay,
            e.Location ?? "", e.Description ?? "", popups, byDefault, e.RecurringEventId is not null);
    }

    /// New event, or changes to one. Edits go through get-change-update, so what Glance doesn't show (guests, video links) stays.
    /// A repeating event's occurrence has its own id: only that one changes.
    public static async Task Save(CalEvent? existing, EventDraft d)
    {
        var s = service ?? throw new InvalidOperationException("Google Calendar isn't connected.");
        var ev = existing is null ? new Event() : await s.Events.Get(existing.Calendar.Id, existing.Id).ExecuteAsync();
        ev.Summary = d.Title;
        ev.Location = d.Location;
        ev.Description = d.Description;
        ev.Start = Time(d.Start, d.AllDay);
        ev.End = Time(d.AllDay ? d.End.Date.AddDays(1) : d.End, d.AllDay); // all-day: Google's end date is the day after
        ev.Reminders = d.Reminder switch
        {
            null => new Event.RemindersData { UseDefault = true },
            < 0 => new Event.RemindersData { UseDefault = false, Overrides = [] },
            var m => new Event.RemindersData { UseDefault = false, Overrides = [new EventReminder { Method = "popup", Minutes = m }] },
        };
        if (existing is null) await s.Events.Insert(ev, d.Calendar.Id).ExecuteAsync();
        else await s.Events.Update(ev, existing.Calendar.Id, existing.Id).ExecuteAsync();
        Db.Info(existing is null ? "calendar event added" : "calendar event changed");
    }

    // No time zone name: the offset in the time says it (Google wants IANA names, Windows has its own).
    static EventDateTime Time(DateTime t, bool allDay) => allDay
        ? new EventDateTime { Date = t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
        : new EventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(t) };

    public static async Task Delete(CalEvent e)
    {
        var s = service ?? throw new InvalidOperationException("Google Calendar isn't connected.");
        await s.Events.Delete(e.Calendar.Id, e.Id).ExecuteAsync();
        Db.Info("calendar event deleted");
    }

    // ---- Reminder popups: the next two days, refreshed every few minutes while Glance runs ----

    static List<CalEvent> upcoming = [];
    static readonly HashSet<string> fired = [];

    public static async Task RefreshUpcoming()
    {
        if (!await Resume()) return;
        try { upcoming = await Events(DateTime.Now.AddHours(-1), DateTime.Now.AddDays(2)); }
        catch (Exception e) when (IsGoogleError(e)) { Db.Warn("calendar refresh: " + Why(e)); } // keep the last list
    }

    /// Popups that came due in the last two minutes, each once. Not older: a restart mustn't replay the day.
    public static List<(CalEvent Event, int Minutes)> DueAlerts(DateTime now) =>
        (from e in upcoming
         from m in e.Popups
         let at = e.Start.AddMinutes(-m)
         where at <= now && at > now.AddMinutes(-2) && fired.Add($"{e.Id}|{e.Start:O}|{m}")
         select (e, m)).ToList();

    /// Google's sign-in, encrypted for this Windows user. One file per key; the default store would keep it in plain text.
    sealed class ProtectedStore : IDataStore
    {
        static readonly string Dir = Path.Combine(Settings.Dir, "google");

        static string FileFor<T>(string key) =>
            Path.Combine(Dir, System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(typeof(T).FullName + "-" + key)))[..20] + ".dat");

        public Task StoreAsync<T>(string key, T value)
        {
            Directory.CreateDirectory(Dir);
            var json = Encoding.UTF8.GetBytes(NewtonsoftJsonSerializer.Instance.Serialize(value));
            File.WriteAllBytes(FileFor<T>(key), ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
            return Task.CompletedTask;
        }

        public Task DeleteAsync<T>(string key)
        {
            File.Delete(FileFor<T>(key));
            return Task.CompletedTask;
        }

        public Task<T> GetAsync<T>(string key)
        {
            var f = FileFor<T>(key);
            if (!File.Exists(f)) return Task.FromResult<T>(default!);
            try
            {
                var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(f), null, DataProtectionScope.CurrentUser));
                return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
            }
            catch (CryptographicException) { return Task.FromResult<T>(default!); } // another user's, or damaged: sign in again
        }

        public Task ClearAsync()
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            return Task.CompletedTask;
        }
    }
}
