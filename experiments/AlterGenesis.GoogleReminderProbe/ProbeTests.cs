using System.Net;
using System.Text;
using AlterGenesis.NotificationProbe.Google;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using CalendarEvent = Google.Apis.Calendar.v3.Data.Event;

namespace AlterGenesis.GoogleReminderProbe;

internal static class ProbeTests
{
    private static EventReminder Popup(int minutes) => new() { Method = "popup", Minutes = minutes };
    private static CalendarEvent TestEvent(bool useDefault, params EventReminder[] reminders) => new()
    {
        Id = "fake-event", Summary = "テスト", Status = "confirmed",
        Start = new EventDateTime { DateTimeRaw = "2026-10-29T17:15:00+09:00" },
        Reminders = new CalendarEvent.RemindersData { UseDefault = useDefault, Overrides = reminders }
    };
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
    internal static async Task<int> RunAsync(string report)
    {
        var results = new List<object>();
        var failed = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); results.Add(new { Name = name, Passed = true }); }
            catch (Exception exception) { failed++; results.Add(new { Name = name, Passed = false, Error = exception.GetType().Name }); }
        }
        Task CheckValue(string name, Action action) => Check(name, () => { action(); return Task.CompletedTask; });
        await CheckValue("Individual two-minute reminder gives 17:13 JST / 08:13 UTC", () =>
        {
            var result = ReminderCalculation.From(TestEvent(false, Popup(2)), [Popup(10)]);
            Require(result.Source == "Override" && result.Outcome == "Calculated" && result.PopupReminders.Length == 1
                && result.PopupReminders[0].DueAtUtc == DateTimeOffset.Parse("2026-10-29T08:13:00Z"));
        });
        await CheckValue("Default reminders override stale event override fields", () =>
        {
            var result = ReminderCalculation.From(TestEvent(true, Popup(2)), [Popup(10)]);
            Require(result.Source == "Default" && result.PopupReminders.Single().MinutesBefore == 10);
        });
        await CheckValue("Explicit no-reminder never falls back to calendar defaults or event start", () =>
        {
            var result = ReminderCalculation.From(TestEvent(false), [Popup(10)]);
            Require(result.Source == "Override" && result.Outcome == "NoReminders" && result.PopupReminders.Length == 0);
        });
        await CheckValue("Empty calendar defaults remain identifiable as default with no reminders", () =>
        {
            var result = ReminderCalculation.From(TestEvent(true), null);
            Require(result.Source == "Default" && result.Outcome == "NoReminders");
        });
        await CheckValue("Email-only reminders are not converted to popups", () =>
        {
            var result = ReminderCalculation.From(TestEvent(false, new EventReminder { Method = "email", Minutes = 2 }), null);
            Require(result.Outcome == "NoPopupReminders" && result.EmailReminders == 1 && result.PopupReminders.Length == 0);
        });
        await CheckValue("Multiple popup times survive, duplicate times collapse, email stays separate", () =>
        {
            var result = ReminderCalculation.From(TestEvent(false, Popup(2), Popup(10), Popup(2), new() { Method = "email", Minutes = 30 }), null);
            Require(result.PopupReminders.Select(item => item.MinutesBefore).SequenceEqual(new[] { 10, 2 }) && result.EmailReminders == 1);
        });
        await CheckValue("Missing reminder fields are unknown rather than no-reminder", () =>
        {
            var item = TestEvent(false); item.Reminders = null;
            Require(ReminderCalculation.From(item, null).Outcome == "MissingReminderSettings");
        });
        await CheckValue("Malformed reminders do not partially schedule", () =>
        {
            foreach (var bad in new EventReminder[] { Popup(-1), Popup(40321), new() { Method = "popup" } })
            {
                var result = ReminderCalculation.From(TestEvent(false, Popup(2), bad), null);
                Require(result.Outcome == "InvalidReminderSettings" && result.PopupReminders.Length == 0);
            }
        });
        await CheckValue("Unknown method is counted without losing a valid popup", () =>
        {
            var result = ReminderCalculation.From(TestEvent(false, Popup(2), new() { Method = "sms" }), null);
            Require(result.Outcome == "Calculated" && result.PopupReminders.Single().MinutesBefore == 2 && result.UnsupportedReminders == 1);
            var onlyUnknown = ReminderCalculation.From(TestEvent(false, new EventReminder { Method = "sms" }), null);
            Require(onlyUnknown.Outcome == "UnsupportedReminders" && onlyUnknown.PopupReminders.Length == 0);
        });
        await CheckValue("Zero and four-week boundary reminders calculate", () =>
        {
            var result = ReminderCalculation.From(TestEvent(false, Popup(0), Popup(40320)), null);
            Require(result.PopupReminders.Length == 2 && result.PopupReminders[1].DueAtUtc == result.StartAtUtc);
        });
        await CheckValue("All-day times are deferred rather than assuming 09:00", () =>
        {
            var item = TestEvent(false, Popup(2)); item.Start = new() { Date = "2026-10-29" };
            Require(ReminderCalculation.From(item, null).Outcome == "AllDayDeferred");
        });
        await CheckValue("Time offset is respected independently of Windows timezone", () =>
        {
            var item = TestEvent(false, Popup(2)); item.Start.DateTimeRaw = "2026-10-29T17:15:00+05:30";
            Require(ReminderCalculation.From(item, null).PopupReminders.Single().DueAtUtc == DateTimeOffset.Parse("2026-10-29T11:43:00Z"));
        });
        await CheckValue("Missing timezone offset is not interpreted using local timezone", () =>
        {
            var item = TestEvent(false, Popup(2)); item.Start.DateTimeRaw = "2026-10-29T17:15:00";
            Require(ReminderCalculation.From(item, null).Outcome == "InvalidStart");
        });
        await CheckValue("Cancelled event produces no calculation", () =>
        {
            var item = TestEvent(false, Popup(2)); item.Status = "cancelled";
            Require(ReminderCalculation.From(item, null).Outcome == "Cancelled");
        });
        await CheckValue("Changed reminder and changed calendar default recalculate without event updated timestamp", () =>
        {
            var item = TestEvent(false, Popup(2));
            var before = ReminderCalculation.From(item, null);
            item.Reminders.Overrides = [Popup(3)];
            Require(ReminderCalculation.From(item, null).PopupReminders.Single().DueAtUtc != before.PopupReminders.Single().DueAtUtc);
            item.Reminders.UseDefault = true;
            Require(ReminderCalculation.From(item, [Popup(5)]).PopupReminders.Single().MinutesBefore == 5);
            Require(ReminderCalculation.From(item, [Popup(6)]).PopupReminders.Single().MinutesBefore == 6);
        });

        var calendarDefaults = """{"defaultReminders":[{"method":"popup","minutes":10}]}""";
        var firstPage = """{"items":[{"id":"a","summary":"テスト","start":{"dateTime":"2026-10-29T08:15:00Z"},"reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":2}]}},{"id":"other","summary":"テスト外","start":{"dateTime":"2026-10-29T08:15:00Z"},"reminders":{"useDefault":false}}],"nextPageToken":"page2"}""";
        var secondPage = """{"items":[{"id":"b","summary":"テスト","start":{"dateTime":"2026-10-29T08:15:00Z"},"reminders":{"useDefault":true}},{"id":"c","summary":"テスト","start":{"dateTime":"2026-10-29T08:15:00Z"},"reminders":{"useDefault":false}}]}""";
        await Check("Calendar defaults fetched once apply to later pages even when all event pages omit defaults", async () =>
        {
            using var handler = new FakeHandler(calendarDefaults, firstPage, secondPage);
            using var service = Service(handler);
            var values = await ReminderReader.ReadAsync(service, "fake-calendar", DateTimeOffset.Parse("2026-10-29T00:00:00+09:00"), "テスト", default);
            Require(handler.CalendarCalls == 1 && handler.PageCalls == 2 && values.Length == 3 && values[0].Source == "Override"
                && values[1].Source == "Default" && values[1].PopupReminders.Single().MinutesBefore == 10 && values[2].Outcome == "NoReminders");
        });
        await Check("Successful calendar response with no defaults means default/no-reminder", async () =>
        {
            using var handler = new FakeHandler("{}", secondPage);
            using var service = Service(handler);
            var values = await ReminderReader.ReadAsync(service, "fake-calendar", DateTimeOffset.Parse("2026-10-29T00:00:00+09:00"), "テスト", default);
            Require(values[0].Source == "Default" && values[0].Outcome == "NoReminders");
        });
        await Check("Calendar metadata failure does not turn into no-reminder or read events", async () =>
        {
            using var handler = new FakeHandler(null, secondPage);
            using var service = Service(handler);
            try { await ReminderReader.ReadAsync(service, "fake-calendar", DateTimeOffset.UtcNow, "テスト", default); }
            catch (global::Google.GoogleApiException) { Require(handler.CalendarCalls == 1 && handler.PageCalls == 0); return; }
            throw new InvalidOperationException();
        });
        await Check("Earlier-day overlapping event is excluded from the start-day probe", async () =>
        {
            var overlapPage = """{"items":[{"id":"prior-day","summary":"テスト","start":{"dateTime":"2026-10-28T14:00:00Z"},"reminders":{"useDefault":true}}]}""";
            using var handler = new FakeHandler(calendarDefaults, overlapPage);
            using var service = Service(handler);
            var values = await ReminderReader.ReadAsync(service, "fake-calendar", DateTimeOffset.Parse("2026-10-29T00:00:00+09:00"), "テスト", default);
            Require(values.Length == 0);
        });
        await Check("Later-page failure rejects entire read", async () =>
        {
            using var handler = new FakeHandler(calendarDefaults, firstPage, null);
            using var service = Service(handler);
            try { await ReminderReader.ReadAsync(service, "fake-calendar", DateTimeOffset.UtcNow, "テスト", default); }
            catch (global::Google.GoogleApiException) { Require(handler.PageCalls == 2); return; }
            throw new InvalidOperationException();
        });
        await Check("Repeated pagination token rejects read", async () =>
        {
            using var handler = new FakeHandler(calendarDefaults, "{\"nextPageToken\":\"same\"}", "{\"nextPageToken\":\"same\"}");
            using var service = Service(handler);
            try { await ReminderReader.ReadAsync(service, "fake-calendar", DateTimeOffset.UtcNow, "テスト", default); }
            catch (ProbeException exception) when (exception.Code == "RepeatedPageToken") { return; }
            throw new InvalidOperationException();
        });
        var fixture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "oauth-reminder-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            await Check("Isolated encrypted tokens round-trip with no plaintext and preserve damaged bytes", async () =>
            {
                var store = new ProtectedGoogleStore(Path.Combine(fixture, "google"));
                await store.StoreAsync("fake", new TokenResponse { AccessToken = "fake-access", RefreshToken = "fake-refresh" });
                Require((await store.GetAsync<TokenResponse>("fake")).RefreshToken == "fake-refresh");
                var path = store.PathFor<TokenResponse>("fake");
                Require(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("fake-refresh"));
                await File.WriteAllBytesAsync(path, [1, 2, 3]);
                try { await store.GetAsync<TokenResponse>("fake"); }
                catch (System.Security.Cryptography.CryptographicException) { Require((await File.ReadAllBytesAsync(path)).SequenceEqual(new byte[] { 1, 2, 3 })); return; }
                throw new InvalidOperationException();
            });
            await Check("Missing selection never broadens to all calendars", async () =>
            {
                using var connection = new ProbeConnection(fixture);
                try { await connection.SelectedCalendarIdsAsync(); }
                catch (ProbeException exception) when (exception.Code == "CalendarSelectionRequired") { return; }
                throw new InvalidOperationException();
            });
            await Check("Preserved selection type and key remain compatible", async () =>
            {
                var store = new ProtectedGoogleStore(Path.Combine(fixture, "google"));
                await store.StoreAsync("catalog", new GoogleCalendarSnapshot(1, [new("a", "Fake", "#71859C", true)], ["a"]));
                using var connection = new ProbeConnection(fixture);
                Require((await connection.SelectedCalendarIdsAsync()).SequenceEqual(new[] { "a" }));
            });
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(report))!) + Path.DirectorySeparatorChar;
            var resolvedFixture = Path.GetFullPath(fixture);
            if (!resolvedFixture.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolvedFixture).StartsWith("oauth-reminder-fixture-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup target");
            Directory.Delete(resolvedFixture, recursive: true);
        }
        await Program.WriteReportAsync(report, new { Passed = failed == 0, Count = results.Count, Failed = failed,
            UsesRealCredentials = false, NotificationsEnabled = false, Checks = results });
        Console.WriteLine($"SelfTest: {results.Count - failed}/{results.Count} passed; notifications=disabled");
        return failed == 0 ? 0 : 1;
    }

    private static CalendarService Service(FakeHandler handler) => new(new BaseClientService.Initializer
    { ApplicationName = "AlterGenesis-Reminder-IsolatedTest", HttpClientFactory = new FakeFactory(handler) });
    private sealed class FakeFactory(FakeHandler handler) : IHttpClientFactory
    {
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) => new(new ConfigurableMessageHandler(handler));
    }
    private sealed class FakeHandler(string? calendarDefaults, params string?[] pages) : HttpMessageHandler
    {
        internal int CalendarCalls;
        internal int PageCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Require(request.Method == HttpMethod.Get && request.RequestUri.Host == "www.googleapis.com");
            string? page;
            if (request.RequestUri.AbsolutePath.Contains("/calendarList/", StringComparison.Ordinal))
            {
                Require(query.Contains("defaultReminders"));
                CalendarCalls++;
                page = calendarDefaults;
            }
            else
            {
                Require(query.Contains("singleEvents=true") && query.Contains("reminders")
                    && query.Contains("timeMin=") && query.Contains("timeMax=") && !query.Contains("updatedMin"));
                page = pages[PageCalls++];
            }
            return Task.FromResult(new HttpResponseMessage(page is null ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            { Content = new StringContent(page ?? "{\"error\":{\"code\":400,\"message\":\"Fake failure\"}}", Encoding.UTF8, "application/json") });
        }
    }
}
