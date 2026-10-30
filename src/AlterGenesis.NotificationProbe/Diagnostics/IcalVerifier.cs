using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AlterGenesis.NotificationProbe.Calendars;
using AlterGenesis.NotificationProbe.Notifications;

namespace AlterGenesis.NotificationProbe.Diagnostics;

internal static class IcalVerifier
{
    public static async Task<bool> RunAsync(string reportPath)
    {
        var checks = new List<object>();
        var passed = true;
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!,
            "ical-checks-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(root);

        void Check(string name, bool result)
        {
            checks.Add(new { Name = name, Passed = result });
            passed &= result;
        }

        var fakeUrl = "https://calendar.google.com/calendar/ical/example/private-fake/basic.ics";
        var subscription = IcalCalendarSubscription.Create("Sample Calendar", "#71859C", fakeUrl);
        Check("accessible identity hides secret", subscription.ToString() == "Sample Calendar"
            && !subscription.ToString().Contains("private-fake", StringComparison.Ordinal));
        var store = new IcalCalendarStore(Path.Combine(root, "roundtrip"));
        Check("encrypted save", store.TrySave([subscription]));
        var storedBytes = File.ReadAllBytes(store.FilePath);
        var storedText = Encoding.UTF8.GetString(storedBytes);
        Check("secret absent from ciphertext", !storedText.Contains("private-fake", StringComparison.Ordinal));
        Check("name absent from ciphertext", !storedText.Contains("Sample Calendar", StringComparison.Ordinal));
        var restored = store.Load();
        Check("encrypted roundtrip", restored.Count == 1 && restored[0] == subscription);

        var brokenStore = new IcalCalendarStore(Path.Combine(root, "broken"));
        Directory.CreateDirectory(Path.GetDirectoryName(brokenStore.FilePath)!);
        var brokenBytes = Encoding.UTF8.GetBytes("not encrypted calendar data");
        File.WriteAllBytes(brokenStore.FilePath, brokenBytes);
        Check("broken data protected", brokenStore.Load().Count == 0 && brokenStore.LoadFailed);
        Check("broken data refuses overwrite", !brokenStore.TrySave([subscription])
            && File.ReadAllBytes(brokenStore.FilePath).SequenceEqual(brokenBytes));

        const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//ALTER GENESIS Test//EN\r\n"
            + "X-WR-CALNAME:Sample Calendar\r\n"
            + "BEGIN:VEVENT\r\nUID:one\r\nDTSTART:20261029T120000Z\r\nSUMMARY:One\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:two\r\nDTSTART:20261030T120000Z\r\nSUMMARY:Two\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n";
        using (var client = new IcalFeedClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new StringContent(ics, Encoding.UTF8, "text/calendar") })))
        {
            var preview = await client.ReadAsync(fakeUrl);
            Check("calendar name parsed", preview.Name == "Sample Calendar");
            Check("events parsed", preview.EventCount == 2);
        }

        const string occurrenceIcs = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//ALTER GENESIS Test//EN\r\n"
            + "X-WR-CALNAME:Schedule Fixture\r\n"
            + "BEGIN:VEVENT\r\nUID:single\r\nDTSTART:20261029T120000Z\r\nSUMMARY:Single\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:daily\r\nDTSTART:20261029T130000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Daily\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:all-day\r\nDTSTART;VALUE=DATE:20261030\r\nSUMMARY:All Day\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:cancelled\r\nDTSTART:20261030T140000Z\r\nSTATUS:CANCELLED\r\nSUMMARY:Cancelled\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n";
        var testZone = TimeZoneInfo.CreateCustomTimeZone("ALTER-GENESIS-ICAL-TEST",
            TimeSpan.FromHours(9), "ALTER-GENESIS-ICAL-TEST", "ALTER-GENESIS-ICAL-TEST");
        var baseline = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        var through = baseline.AddDays(5);
        using (var client = new IcalFeedClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new StringContent(occurrenceIcs, Encoding.UTF8, "text/calendar") })))
        {
            var snapshot = await client.ReadEventsAsync(fakeUrl, baseline, through, testZone);
            Check("recurrences expanded and cancelled events ignored", snapshot.Occurrences.Count == 5
                && snapshot.Occurrences.Count(item => item.EventUid == "daily") == 3
                && snapshot.Occurrences.All(item => item.EventUid != "cancelled"));
            var allDay = snapshot.Occurrences.Single(item => item.EventUid == "all-day");
            Check("all-day date preserved", allDay.IsAllDay
                && allDay.AllDayDate == new DateOnly(2026, 10, 30));

            var scheduled = CalendarNotificationFactory.Create(subscription, snapshot,
                baseline, through, testZone, "終日", "（無題）");
            var repeated = CalendarNotificationFactory.Create(subscription, snapshot,
                baseline, through, testZone, "終日", "（無題）");
            Check("stable occurrence ids prevent duplicates", scheduled.Count == 5
                && scheduled.Select(item => item.Item.Id).SequenceEqual(repeated.Select(item => item.Item.Id))
                && scheduled.Select(item => item.Item.Id).Distinct(StringComparer.Ordinal).Count() == 5);
            var allDayNotification = scheduled.Single(item => item.Item.Title == "All Day");
            Check("all-day notification waits until 09:00 local", allDayNotification.Item.StartTime == "終日"
                && allDayNotification.Item.StartTimeResourceKey == "Text.AllDay"
                && allDayNotification.DueAtUtc == new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero));

            var memory = new MemoryStore();
            var now = baseline;
            var schedule = new NotificationSchedule(memory, [], () => now);
            Check("calendar schedule is replaced without duplicates", schedule.TryReplaceCalendarSchedule(
                    subscription.Id, scheduled)
                && schedule.TryReplaceCalendarSchedule(subscription.Id, scheduled)
                && schedule.Items.Count == scheduled.Count && schedule.HasPending);
            Check("removed future events leave the schedule", schedule.TryReplaceCalendarSchedule(
                    subscription.Id, scheduled.Take(2).ToArray())
                && schedule.Items.Count == 2);
            Check("due calendar events become visible once", schedule.TryReplaceCalendarSchedule(
                    subscription.Id, scheduled)
                && schedule.TryReleaseDue(out var early) && early.Count == 0);
            now = scheduled.Min(item => item.DueAtUtc);
            Check("due calendar event is released", schedule.TryReleaseDue(out var due)
                && due.Count == scheduled.Count(item => item.DueAtUtc <= now)
                && schedule.TryReleaseDue(out var repeatedDue) && repeatedDue.Count == 0);

            var scheduledStore = new NotificationStore(Path.Combine(root, "scheduled-roundtrip"));
            Check("scheduled source survives restart", scheduledStore.TrySave(schedule.Items)
                && new NotificationStore(Path.Combine(root, "scheduled-roundtrip")).Load()
                    .Any(item => item.Status == NotificationStatus.Scheduled
                        && item.SourceId == subscription.Id
                        && item.StartTimeResourceKey == "Text.AllDay"));
        }

        using (var client = new IcalFeedClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))))
        {
            Check("access denied classified", await HasError(client, fakeUrl, IcalFeedError.AccessDenied));
        }
        Check("non Google URL rejected", ThrowsArgument(() =>
            IcalFeedClient.ValidateSecretUrl("https://example.com/calendar.ics")));
        Check("plain HTTP rejected", ThrowsArgument(() =>
            IcalFeedClient.ValidateSecretUrl("http://calendar.google.com/calendar.ics")));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            Passed = passed,
            CheckCount = checks.Count,
            Checks = checks,
            FixtureDirectory = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, root),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static async Task<bool> HasError(IcalFeedClient client, string url, IcalFeedError expected)
    {
        try { _ = await client.ReadAsync(url); }
        catch (IcalFeedException exception) { return exception.Error == expected && !exception.Message.Contains(url, StringComparison.Ordinal); }
        return false;
    }

    private static bool ThrowsArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return true; }
        return false;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }

    private sealed class MemoryStore : INotificationStore
    {
        public bool TrySave(IReadOnlyList<SavedNotification> items) => true;
    }
}
