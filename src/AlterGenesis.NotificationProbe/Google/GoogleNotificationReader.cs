using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using AlterGenesis.GoogleReminderProbe;
using AlterGenesis.NotificationProbe.Models;
using AlterGenesis.NotificationProbe.Notifications;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using CalendarEvent = Google.Apis.Calendar.v3.Data.Event;

namespace AlterGenesis.NotificationProbe.Google;

internal sealed record GoogleNotificationRead(IReadOnlyList<ScheduledNotification> Items, int DeferredAllDay);

internal static class GoogleNotificationReader
{
    internal static string SourceId(string calendarId) => "google-" + Hash(calendarId);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static async Task<GoogleNotificationRead> ReadAsync(CalendarService service,
        GoogleCalendarSnapshot snapshot, IReadOnlyList<SavedNotification> previous,
        DateTimeOffset now, TimeZoneInfo zone, string untitled, CancellationToken cancellationToken)
    {
        var output = new List<ScheduledNotification>();
        var allDay = 0;
        // Keep due reminders already waiting on disk, including a short offline interval.
        var from = previous.Where(item => item.Status == NotificationStatus.Scheduled)
            .Select(item => item.DueAtUtc!.Value).Append(now).Min();
        if (from < now.AddDays(-30)) from = now.AddDays(-30);
        foreach (var calendar in snapshot.Calendars.Where(item => snapshot.SelectedIds.Contains(item.Id)))
        {
            var defaultsRequest = service.CalendarList.Get(calendar.Id);
            defaultsRequest.Fields = "defaultReminders(method,minutes)";
            var defaults = (await defaultsRequest.ExecuteAsync(cancellationToken)).DefaultReminders;
            var pages = new HashSet<string>(StringComparer.Ordinal);
            var eventIds = new HashSet<string>(StringComparer.Ordinal);
            string? page = null;
            do
            {
                var request = service.Events.List(calendar.Id);
                request.TimeMinDateTimeOffset = from;
                // A reminder can precede its event by 28 days. Fetch starts beyond the due horizon.
                request.TimeMaxDateTimeOffset = now.AddDays(58);
                request.SingleEvents = true;
                request.ShowDeleted = false;
                request.MaxResults = 250;
                request.PageToken = page;
                request.Fields = "nextPageToken,items(id,summary,status,htmlLink,start(date,dateTime,timeZone),reminders(useDefault,overrides(method,minutes)))";
                var response = await request.ExecuteAsync(cancellationToken);
                foreach (var item in response.Items ?? [])
                {
                    if (string.IsNullOrWhiteSpace(item.Id) || !eventIds.Add(item.Id))
                        throw new InvalidDataException("Missing or repeated event identity.");
                    if (!string.IsNullOrEmpty(item.Start?.Date)) allDay++;
                    output.AddRange(Create(calendar, item, defaults, now, previous, zone, untitled));
                }
                page = response.NextPageToken;
                if (eventIds.Count > 10000 || pages.Count >= 100
                    || !string.IsNullOrEmpty(page) && !pages.Add(page))
                    throw new InvalidDataException("Event page limit exceeded.");
            } while (!string.IsNullOrEmpty(page));
        }
        return new(output.OrderBy(item => item.DueAtUtc).ThenBy(item => item.Item.Id, StringComparer.Ordinal).ToArray(), allDay);
    }

    internal static IReadOnlyList<ScheduledNotification> Create(GoogleCalendarDescriptor calendar,
        CalendarEvent item, IList<EventReminder>? defaults, DateTimeOffset now,
        IReadOnlyList<SavedNotification> previous, TimeZoneInfo zone, string untitled)
    {
        var calculation = ReminderCalculation.From(item, defaults);
        if (calculation.Outcome is "InvalidStart" or "InvalidReminderSettings" or "MissingReminderSettings")
            throw new InvalidDataException("Cannot safely interpret event reminders.");
        if (calculation.PopupReminders.Length == 0) return [];
        if (string.IsNullOrWhiteSpace(item.Id)) throw new InvalidDataException("Missing event identity.");
        var start = calculation.StartAtUtc!.Value;
        var source = SourceId(calendar.Id);
        var title = (item.Summary ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (title.Length == 0) title = untitled;
        var color = Regex.IsMatch(calendar.Color ?? "", "^#[0-9a-fA-F]{6}$") ? calendar.Color : "#71859C";
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color!));
        brush.Freeze();
        var eventUri = Uri.TryCreate(item.HtmlLink, UriKind.Absolute, out var link)
            && link.Scheme == "https" && link.Host is "calendar.google.com" or "www.google.com" or "google.com"
            ? link : new Uri("https://calendar.google.com/calendar/u/0/r");
        var existing = previous.Where(value => value.Status == NotificationStatus.Scheduled)
            .Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        return calculation.PopupReminders.Select(reminder =>
        {
            var id = "google-" + Hash(JsonSerializer.Serialize(new[] { source, item.Id,
                start.ToString("O", CultureInfo.InvariantCulture), reminder.MinutesBefore.ToString(CultureInfo.InvariantCulture) }));
            return new ScheduledNotification(new NotificationItem(
                TimeZoneInfo.ConvertTime(start, zone).ToString("HH:mm", CultureInfo.InvariantCulture), title, brush, eventUri)
                { Id = id }, reminder.DueAtUtc, source);
        }).Where(value => value.DueAtUtc < now.AddDays(30)
            && (value.DueAtUtc >= now || existing.Contains(value.Item.Id))).ToArray();
    }
}
