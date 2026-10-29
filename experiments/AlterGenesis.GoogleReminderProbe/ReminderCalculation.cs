using System.Globalization;
using System.Text.RegularExpressions;
using Google.Apis.Calendar.v3.Data;
using CalendarEvent = Google.Apis.Calendar.v3.Data.Event;

namespace AlterGenesis.GoogleReminderProbe;

internal sealed record ReminderTime(int MinutesBefore, DateTimeOffset DueAtUtc);
internal sealed record ReminderCalculation(string Source, string Outcome, DateTimeOffset? StartAtUtc,
    ReminderTime[] PopupReminders, int EmailReminders, int UnsupportedReminders)
{
    internal static ReminderCalculation From(CalendarEvent item, IList<EventReminder>? defaults)
    {
        ReminderCalculation Empty(string source, string outcome, DateTimeOffset? start = null) => new(source, outcome, start, [], 0, 0);
        if (item.Status == "cancelled") return Empty("Unavailable", "Cancelled");
        if (!string.IsNullOrEmpty(item.Start?.Date)) return Empty("Unavailable", "AllDayDeferred");
        var raw = item.Start?.DateTimeRaw;
        if (string.IsNullOrEmpty(raw) || !Regex.IsMatch(raw, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)
            || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            return Empty("Unavailable", "InvalidStart");
        start = start.ToUniversalTime();
        if (item.Reminders?.UseDefault is null) return Empty("Unavailable", "MissingReminderSettings", start);
        var useDefault = item.Reminders.UseDefault == true;
        var source = useDefault ? "Default" : "Override";
        var effective = useDefault ? defaults : item.Reminders.Overrides;
        if (effective is null || effective.Count == 0) return Empty(source, "NoReminders", start);
        if (effective.Any(reminder => reminder is null
            || (reminder.Method is "popup" or "email" && reminder.Minutes is null or < 0 or > 40320)))
            return Empty(source, "InvalidReminderSettings", start);
        var minutes = effective.Where(reminder => reminder.Method == "popup")
            .Select(reminder => reminder.Minutes!.Value).Distinct().OrderDescending().ToArray();
        ReminderTime[] times;
        try { times = minutes.Select(value => new ReminderTime(value, start.AddMinutes(-value))).ToArray(); }
        catch (ArgumentOutOfRangeException) { return Empty(source, "InvalidStart", start); }
        var unsupported = effective.Count(reminder => reminder.Method is not ("popup" or "email"));
        return new(source, times.Length > 0 ? "Calculated" : unsupported > 0 ? "UnsupportedReminders" : "NoPopupReminders", start, times,
            effective.Count(reminder => reminder.Method == "email"), unsupported);
    }
}
