using System.Security.Cryptography;
using System.Text;
using AlterGenesis.NotificationProbe.Models;
using AlterGenesis.NotificationProbe.Notifications;

namespace AlterGenesis.NotificationProbe.Calendars;

internal static class CalendarNotificationFactory
{
    private static readonly Uri GoogleCalendarHome = new("https://calendar.google.com/calendar/u/0/r");

    internal static IReadOnlyList<ScheduledNotification> Create(
        IcalCalendarSubscription subscription,
        IcalCalendarSnapshot snapshot,
        DateTimeOffset nowUtc,
        DateTimeOffset throughUtc,
        TimeZoneInfo localTimeZone,
        string allDayText,
        string untitledText)
    {
        var items = new List<ScheduledNotification>();
        foreach (var occurrence in snapshot.Occurrences)
        {
            var dueAtUtc = occurrence.AllDayDate is { } date
                ? AtLocalTime(date, new TimeOnly(9, 0), localTimeZone)
                : occurrence.StartsAtUtc;
            if (dueAtUtc < nowUtc || dueAtUtc >= throughUtc) continue;

            var title = NormalizeTitle(occurrence.Title, untitledText);
            var startTime = occurrence.IsAllDay
                ? allDayText
                : TimeZoneInfo.ConvertTime(occurrence.StartsAtUtc, localTimeZone).ToString("HH:mm");
            var id = CreateStableId(subscription.Id, occurrence.EventUid, occurrence.StartsAtUtc,
                occurrence.AllDayDate);
            var item = new NotificationItem(startTime, title, subscription.ColorBrush, GoogleCalendarHome)
            {
                Id = id,
            };
            items.Add(new ScheduledNotification(item, dueAtUtc, subscription.Id));
        }

        return items.OrderBy(item => item.DueAtUtc).ThenBy(item => item.Item.Id, StringComparer.Ordinal).ToArray();
    }

    private static DateTimeOffset AtLocalTime(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static string NormalizeTitle(string title, string fallback)
    {
        var normalized = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string CreateStableId(string subscriptionId, string eventUid,
        DateTimeOffset startsAtUtc, DateOnly? allDayDate)
    {
        var occurrence = allDayDate?.ToString("yyyy-MM-dd") ?? startsAtUtc.ToUniversalTime().ToString("O");
        var material = $"{subscriptionId}\n{eventUid}\n{occurrence}";
        return "ical-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
