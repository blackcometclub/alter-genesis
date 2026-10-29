using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;

namespace AlterGenesis.GoogleReminderProbe;

internal static class ReminderReader
{
    internal static async Task<ReminderCalculation[]> ReadAsync(CalendarService service, string calendarId,
        DateTimeOffset dayStart, string exactTitle, CancellationToken cancellationToken)
    {
        var all = new List<ReminderCalculation>();
        var pageTokens = new HashSet<string>(StringComparer.Ordinal);
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        // Defaults are calendar-level metadata. Fetch once, independently of paged event responses.
        var defaultsRequest = service.CalendarList.Get(calendarId);
        defaultsRequest.Fields = "defaultReminders(method,minutes)";
        var defaults = (await defaultsRequest.ExecuteAsync(cancellationToken)).DefaultReminders;
        string? pageToken = null;
        var pages = 0;
        do
        {
            if (++pages > 100) throw new ProbeException("TooManyPages");
            var request = service.Events.List(calendarId);
            request.TimeMinDateTimeOffset = dayStart;
            request.TimeMaxDateTimeOffset = dayStart.AddDays(1);
            request.TimeZone = "UTC";
            request.SingleEvents = true;
            request.ShowDeleted = false;
            request.Q = exactTitle;
            request.MaxResults = 250;
            request.PageToken = pageToken;
            request.Fields = "nextPageToken,items(id,summary,status,start(date,dateTime,timeZone),reminders(useDefault,overrides(method,minutes)))";
            var response = await request.ExecuteAsync(cancellationToken);
            foreach (var item in response.Items ?? [])
            {
                if (item.Summary != exactTitle) continue;
                if (string.IsNullOrWhiteSpace(item.Id) || !eventIds.Add(item.Id)) throw new ProbeException("InvalidEventIdentity");
                var calculation = ReminderCalculation.From(item, defaults);
                // events.list timeMin filters event END, so exclude an earlier day's overlapping event.
                if (calculation.StartAtUtc is { } start && (start < dayStart || start >= dayStart.AddDays(1))) continue;
                all.Add(calculation);
                if (all.Count > 5000) throw new ProbeException("TooManyEvents");
            }
            pageToken = response.NextPageToken;
            if (!string.IsNullOrEmpty(pageToken) && !pageTokens.Add(pageToken)) throw new ProbeException("RepeatedPageToken");
        } while (!string.IsNullOrEmpty(pageToken));
        return all.ToArray();
    }
}
