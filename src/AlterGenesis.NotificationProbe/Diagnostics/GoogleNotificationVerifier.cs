using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AlterGenesis.NotificationProbe.Google;
using AlterGenesis.NotificationProbe.Notifications;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using CalendarEvent = Google.Apis.Calendar.v3.Data.Event;

namespace AlterGenesis.NotificationProbe.Diagnostics;

internal static class GoogleNotificationVerifier
{
    internal static async Task<bool> RunAsync(string path)
    {
        var checks = new List<object>();
        var failed = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new { Name = name, Passed = true }); }
            catch (Exception ex) { failed++; checks.Add(new { Name = name, Passed = false, Error = ex.GetType().Name }); }
        }
        Task CheckValue(string name, Action action) => Check(name, () => { action(); return Task.CompletedTask; });
        var now = DateTimeOffset.Parse("2026-10-29T09:00:00Z");
        var calendar = new GoogleCalendarDescriptor("fake-calendar", "Fixture", "#71859C", true);
        var sources = new HashSet<string> { GoogleNotificationReader.SourceId(calendar.Id) };
        var snapshot = new GoogleCalendarSnapshot(1, [calendar], [calendar.Id]);
        CalendarEvent Event(params int[] minutes) => new()
        {
            Id = "fixture-event", Summary = "Fixture", Status = "confirmed",
            HtmlLink = "https://calendar.google.com/calendar/event?eid=fake",
            Start = new EventDateTime { DateTimeRaw = now.AddMinutes(10).ToString("O") },
            Reminders = new CalendarEvent.RemindersData { UseDefault = false,
                Overrides = minutes.Select(value => new EventReminder { Method = "popup", Minutes = value }).ToArray() }
        };
        IReadOnlyList<ScheduledNotification> Make(CalendarEvent item, IReadOnlyList<SavedNotification>? previous = null,
            IList<EventReminder>? defaults = null) => GoogleNotificationReader.Create(calendar, item, defaults, now,
                previous ?? [], TimeZoneInfo.Utc, "Untitled");
        await CheckValue("Individual reminder enters the existing schedule and appears once at the exact due time", () =>
        {
            var store = new MemoryStore();
            var schedule = new NotificationSchedule(store, [], () => now);
            var items = Make(Event(2));
            Require(items.Single().DueAtUtc == now.AddMinutes(8) && items.Single().Item.StartTime == "09:10");
            Require(schedule.TryReplaceCalendarSchedules(items.ToArray(), sources));
            now = now.AddMinutes(8).AddTicks(-1);
            Require(schedule.TryReleaseDue(out var early) && early.Count == 0);
            now = now.AddTicks(1);
            Require(schedule.TryReleaseDue(out var due) && due.Count == 1);
            Require(schedule.TryReleaseDue(out due) && due.Count == 0);
            now = DateTimeOffset.Parse("2026-10-29T09:00:00Z");
        });
        await CheckValue("Default, override and no-reminder are distinct", () =>
        {
            var item = Event(2); item.Reminders.UseDefault = true;
            Require(Make(item, defaults: [new() { Method = "popup", Minutes = 5 }]).Single().DueAtUtc == now.AddMinutes(5));
            item.Reminders.UseDefault = false;
            Require(Make(item, defaults: [new() { Method = "popup", Minutes = 5 }]).Single().DueAtUtc == now.AddMinutes(8));
            Require(Make(Event(), defaults: [new() { Method = "popup", Minutes = 5 }]).Count == 0);
        });
        await CheckValue("Multiple reminders remain independent; repeated settings and fetches do not duplicate", () =>
        {
            var item = Event(2, 5, 2);
            var schedule = new NotificationSchedule(new MemoryStore(), [], () => now);
            Require(schedule.TryReplaceCalendarSchedules(Make(item).ToArray(), sources));
            Require(schedule.TryReplaceCalendarSchedules(Make(item, schedule.Items).ToArray(), sources));
            Require(schedule.Items.Count == 2 && schedule.Items.Select(value => value.Id).Distinct().Count() == 2);
        });
        await CheckValue("Moved event, changed reminder and cancellation replace future reservations", () =>
        {
            var item = Event(2);
            var schedule = new NotificationSchedule(new MemoryStore(), [], () => now);
            Require(schedule.TryReplaceCalendarSchedules(Make(item).ToArray(), sources));
            var oldId = schedule.Items.Single().Id;
            item.Start.DateTimeRaw = now.AddMinutes(20).ToString("O");
            Require(schedule.TryReplaceCalendarSchedules(Make(item, schedule.Items).ToArray(), sources));
            Require(schedule.Items.Single().Id != oldId && schedule.Items.Single().DueAtUtc == now.AddMinutes(18));
            item.Reminders.Overrides = [new() { Method = "popup", Minutes = 5 }];
            Require(schedule.TryReplaceCalendarSchedules(Make(item, schedule.Items).ToArray(), sources));
            Require(schedule.Items.Single().DueAtUtc == now.AddMinutes(15));
            item.Status = "cancelled";
            Require(schedule.TryReplaceCalendarSchedules(Make(item, schedule.Items).ToArray(), sources) && schedule.Items.Count == 0);
        });
        await CheckValue("Closed reminders do not reappear after refresh or restart; pending overdue reminders survive", () =>
        {
            var item = Event(2);
            var store = new MemoryStore();
            var schedule = new NotificationSchedule(store, [], () => now);
            Require(schedule.TryReplaceCalendarSchedules(Make(item).ToArray(), sources));
            now = now.AddMinutes(9);
            Require(Make(item, schedule.Items).Count == 1 && Make(item).Count == 0);
            Require(schedule.TryReleaseDue(out var due) && schedule.TryDismiss(due.Single().Id));
            var restarted = new NotificationSchedule(store, store.Items, () => now);
            Require(restarted.TryReplaceCalendarSchedules(Make(item, restarted.Items).ToArray(), sources));
            Require(restarted.Items.Count == 0);
            now = now.AddMinutes(-9);
        });
        await CheckValue("Snooze and visible cards survive refresh; unchecking removes their source", () =>
        {
            var item = Event(10);
            var schedule = new NotificationSchedule(new MemoryStore(), [], () => now);
            Require(schedule.TryReplaceCalendarSchedules(Make(item).ToArray(), sources));
            Require(schedule.TryReleaseDue(out var due) && schedule.TrySnooze(due.Single().Id, 5));
            Require(schedule.TryReplaceCalendarSchedules(Make(item, schedule.Items).ToArray(), sources));
            Require(schedule.Items.Single().Status == NotificationStatus.Snoozed);
            Require(schedule.TryKeepCalendarSources(new HashSet<string>()) && schedule.Items.Count == 0);
        });
        await CheckValue("Failed persistence cannot partially replace or remove a schedule", () =>
        {
            var store = new MemoryStore();
            var schedule = new NotificationSchedule(store, [], () => now);
            Require(schedule.TryReplaceCalendarSchedules(Make(Event(2)).ToArray(), sources));
            var before = schedule.Items.ToArray(); store.Fail = true;
            Require(!schedule.TryReplaceCalendarSchedules([], sources) && !schedule.TryKeepCalendarSources(new HashSet<string>()));
            Require(schedule.Items.SequenceEqual(before));
        });
        await CheckValue("Email-only timed events do not become popup reminders", () =>
        {
            var item = Event(); item.Reminders.Overrides = [new() { Method = "email", Minutes = 2 }];
            Require(Make(item).Count == 0);
        });
        await CheckValue("All-day events notify once at 09:00 local without reminder settings", () =>
        {
            var item = Event();
            item.Start = new() { Date = "2026-10-30" };
            item.Reminders = new() { UseDefault = false, Overrides = [] };
            var japan = TimeZoneInfo.CreateCustomTimeZone("Fixture-JST", TimeSpan.FromHours(9), "Fixture-JST", "Fixture-JST");
            var scheduled = GoogleNotificationReader.Create(calendar, item, null, now, [], japan,
                "Untitled", "All day").Single();
            Require(scheduled.DueAtUtc == DateTimeOffset.Parse("2026-10-30T00:00:00Z")
                && scheduled.Item.StartTime == "All day"
                && scheduled.Item.StartTimeResourceKey == "Text.AllDay");
            var repeated = GoogleNotificationReader.Create(calendar, item, null, now, [], japan,
                "Untitled", "All day").Single();
            Require(repeated.Item.Id == scheduled.Item.Id);
            item.Status = "cancelled";
            Require(GoogleNotificationReader.Create(calendar, item, null, now, [], japan,
                "Untitled", "All day").Count == 0);
        });
        await CheckValue("Past same-day all-day events are not introduced late", () =>
        {
            var item = Event(); item.Start = new() { Date = "2026-10-29" };
            Require(GoogleNotificationReader.Create(calendar, item, null, now.AddMinutes(1), [],
                TimeZoneInfo.Utc, "Untitled", "All day").Count == 0);
        });
        await CheckValue("Corrupt reminder data fails rather than removing existing reservations", () =>
        {
            foreach (var item in new[] { Event(-1), Event(40321), Event(2) })
            {
                if (item.Reminders!.Overrides[0].Minutes == 2) item.Reminders = null;
                var rejected = false; try { Make(item); } catch (InvalidDataException) { rejected = true; }
                Require(rejected);
            }
        });
        await CheckValue("Due horizon includes four-week advance reminders for events more than 30 days away", () =>
        {
            var item = Event(40320); item.Start.DateTimeRaw = now.AddDays(40).ToString("O");
            Require(Make(item).Single().DueAtUtc == now.AddDays(12));
            item.Start.DateTimeRaw = now.AddDays(58).ToString("O");
            Require(Make(item).Count == 0);
        });
        await CheckValue("Calendar identities do not collide and unsafe links use the Calendar home", () =>
        {
            var item = Event(2); item.HtmlLink = "file:///bad.exe";
            var first = Make(item).Single();
            var second = GoogleNotificationReader.Create(calendar with { Id = "another" }, item, null, now, [], TimeZoneInfo.Utc, "Untitled").Single();
            Require(first.Item.Id != second.Item.Id && first.Item.EventUri.Host == "calendar.google.com");
        });
        var jsonEvent = global::Google.Apis.Json.NewtonsoftJsonSerializer.Instance.Serialize(Event(2));
        await Check("All event pages and current defaults are read; no title filter or updatedMin is used", async () =>
        {
            using var handler = new FakeHandler(["{\"nextPageToken\":\"second\",\"items\":[]}", "{\"items\":[" + jsonEvent + "]}"]);
            using var service = Service(handler);
            var result = await GoogleNotificationReader.ReadAsync(service, snapshot, [], now, TimeZoneInfo.Utc, "Untitled", default);
            Require(result.Items.Count == 1 && handler.Pages == 2 && handler.Defaults == 1);
        });
        await Check("Second-page failure cannot supply a partial schedule", async () =>
        {
            using var handler = new FakeHandler(["{\"nextPageToken\":\"second\",\"items\":[" + jsonEvent + "]}", null]);
            using var service = Service(handler);
            var rejected = false;
            try { await GoogleNotificationReader.ReadAsync(service, snapshot, [], now, TimeZoneInfo.Utc, "Untitled", default); }
            catch (global::Google.GoogleApiException) { rejected = true; }
            Require(rejected);
        });
        await Check("No selected calendars performs no event requests", async () =>
        {
            using var handler = new FakeHandler([]);
            using var service = Service(handler);
            var result = await GoogleNotificationReader.ReadAsync(service, snapshot with { SelectedIds = [] }, [], now, TimeZoneInfo.Utc, "Untitled", default);
            Require(result.Items.Count == 0 && handler.Pages == 0 && handler.Defaults == 0);
        });
        await Check("Network failure on restore keeps the saved selection and a working Refresh action", async () =>
        {
            using var handler = new FakeHandler(["{\"items\":[]}"]);
            using var service = Service(handler);
            var connection = new FakeConnection(service, snapshot) { RestoreFailure = new HttpRequestException(
                HttpRequestError.ConnectionError, "Do not expose this message", new System.Net.Sockets.SocketException(
                    (int)System.Net.Sockets.SocketError.AccessDenied)) };
            var schedule = new NotificationSchedule(new MemoryStore(), []);
            using var session = new GoogleNotificationSession(connection, schedule, _ => { }, () => "Untitled",
                (_, _) => Task.CompletedTask);
            await session.RestoreAsync();
            Require(session.Connected && session.Snapshot.SelectedIds.SequenceEqual(snapshot.SelectedIds)
                && session.StatusKey == "Text.GoogleConnectionFailed");
            Require(session.FailureCode == "HttpRequest:ConnectionError:AccessDenied");
            await session.RefreshAsync();
            Require(session.Connected && session.StatusKey == "Text.CalendarEventsRefreshSucceeded");
        });
        await Check("Missing permission requires sign-in rather than pretending Refresh is sufficient", async () =>
        {
            using var service = Service(new FakeHandler([]));
            var connection = new FakeConnection(service, snapshot) { RestoreFailure = new GooglePermissionException() };
            using var session = new GoogleNotificationSession(connection, new NotificationSchedule(new MemoryStore(), []), _ => { }, () => "Untitled");
            await session.RestoreAsync();
            Require(!session.Connected && session.StatusKey == "Text.GooglePermissionsRequired");
        });
        await Check("Transient startup transport failure retries once without interactive login", async () =>
        {
            using var service = Service(new FakeHandler(["{\"items\":[]}"]));
            var connection = new FakeConnection(service, snapshot)
                { RestoreFailure = new HttpRequestException(), RestoreFailuresRemaining = 1 };
            using var session = new GoogleNotificationSession(connection, new NotificationSchedule(new MemoryStore(), []),
                _ => { }, () => "Untitled", (_, _) => Task.CompletedTask);
            await session.RestoreAsync();
            Require(session.Connected && connection.Restores == 2 && connection.Connects == 0
                && session.StatusKey == "Text.CalendarEventsRefreshSucceeded");
        });
        await Check("Startup access denial waits for firewall approval with bounded automatic retries", async () =>
        {
            using var service = Service(new FakeHandler(["{\"items\":[]}"]));
            var waits = new List<TimeSpan>();
            var connection = new FakeConnection(service, snapshot)
            {
                RestoreFailure = new HttpRequestException(HttpRequestError.ConnectionError, "Do not expose this message",
                    new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AccessDenied)),
                RestoreFailuresRemaining = 3
            };
            using var session = new GoogleNotificationSession(connection, new NotificationSchedule(new MemoryStore(), []),
                _ => { }, () => "Untitled", (delay, _) => { waits.Add(delay); return Task.CompletedTask; });
            await session.RestoreAsync();
            Require(session.Connected && connection.Restores == 4 && connection.Connects == 0
                && waits.SequenceEqual(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
                && session.StatusKey == "Text.CalendarEventsRefreshSucceeded");
        });
        await CheckValue("Periodic refresh waits five minutes after completion and never overlaps", () =>
        {
            var cadenceNow = DateTimeOffset.Parse("2026-10-30T01:00:00Z");
            var cadence = new GooglePeriodicRefresh(() => cadenceNow);
            cadence.Update(connected: true, busy: false);
            Require(cadence.IsActive && cadence.NextDueUtc == cadenceNow.AddMinutes(5));
            cadenceNow = cadenceNow.AddMinutes(5).AddTicks(-1);
            Require(!cadence.TryTakeDue(connected: true, busy: false));
            cadenceNow = cadenceNow.AddTicks(1);
            Require(cadence.TryTakeDue(connected: true, busy: false) && cadence.IsActive
                && cadence.NextDueUtc is null);
            Require(!cadence.TryTakeDue(connected: true, busy: true));
            cadence.Update(connected: true, busy: false);
            Require(cadence.NextDueUtc == cadenceNow.AddMinutes(5));
        });
        await CheckValue("Manual work resets periodic cadence and disconnect stops it", () =>
        {
            var cadenceNow = DateTimeOffset.Parse("2026-10-30T02:00:00Z");
            var cadence = new GooglePeriodicRefresh(() => cadenceNow, TimeSpan.FromMinutes(5));
            cadence.Update(connected: true, busy: false);
            cadenceNow = cadenceNow.AddMinutes(4);
            cadence.Update(connected: true, busy: true);
            cadenceNow = cadenceNow.AddMinutes(1);
            cadence.Update(connected: true, busy: false);
            Require(cadence.NextDueUtc == cadenceNow.AddMinutes(5));
            cadenceNow = cadenceNow.AddMinutes(5);
            Require(cadence.TryTakeDue(connected: true, busy: false));
            cadence.Update(connected: false, busy: false);
            Require(!cadence.IsActive && cadence.NextDueUtc is null
                && !cadence.TryTakeDue(connected: false, busy: false));
        });
        await Check("Selection removal with failed notification persistence blocks old timers until a successful retry", async () =>
        {
            using var service = Service(new FakeHandler([]));
            var connection = new FakeConnection(service, snapshot);
            var store = new MemoryStore();
            var schedule = new NotificationSchedule(store, [], () => now);
            Require(schedule.TryReplaceCalendarSchedules(Make(Event(2)).ToArray(), sources));
            using var session = new GoogleNotificationSession(connection, schedule, _ => { }, () => "Untitled");
            store.Fail = true;
            await session.SelectAsync([]);
            Require(session.ScheduleBlocked && schedule.Items.Count == 1);
            store.Fail = false;
            await session.RefreshAsync();
            Require(!session.ScheduleBlocked && schedule.Items.Count == 0);
        });
        await Check("Offline restart honors a saved uncheck even if previous notification removal failed", async () =>
        {
            foreach (var storageFails in new[] { false, true })
            {
                using var service = Service(new FakeHandler([]));
                var connection = new FakeConnection(service, snapshot with { SelectedIds = [] })
                    { RestoreFailure = new HttpRequestException() };
                var store = new MemoryStore();
                var schedule = new NotificationSchedule(store, [], () => now);
                Require(schedule.TryReplaceCalendarSchedules(Make(Event(10)).ToArray(), sources));
                store.Fail = storageFails;
                var published = 0;
                using var session = new GoogleNotificationSession(connection, schedule,
                    items => published += items.Count, () => "Untitled");
                await session.RestoreAsync();
                Require(published == 0 && session.ScheduleBlocked == storageFails);
                Require(storageFails ? schedule.Items.Single().Status == NotificationStatus.Scheduled : schedule.Items.Count == 0);
            }
        });
        await CheckValue("Diagnostic destinations cannot overwrite settings, notifications or encrypted credentials", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "AlterGenesis-guard-fixture");
            foreach (var name in new[] { "settings.json", "notifications.json", "calendars.dpapi",
                "google-notifications/notifications.json", "google/client.dpapi", "extra/../settings.json" })
                Require(!DiagnosticOutputGuard.Allows(root, Path.Combine(root, name)));
            Require(DiagnosticOutputGuard.Allows(root, Path.Combine(root, "settings-layout.json")));
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Passed = failed == 0, Count = checks.Count, Failed = failed,
            UsesRealCredentials = false, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0;
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
    private sealed class MemoryStore : INotificationStore
    {
        internal bool Fail;
        internal IReadOnlyList<SavedNotification> Items = [];
        public bool TrySave(IReadOnlyList<SavedNotification> items) { if (Fail) return false; Items = items.ToArray(); return true; }
    }
    private static CalendarService Service(FakeHandler handler) => new(new BaseClientService.Initializer
    { ApplicationName = "AlterGenesis-Isolated-Test", HttpClientFactory = new FakeFactory(handler) });
    private sealed class FakeConnection(CalendarService service, GoogleCalendarSnapshot snapshot) : IGoogleCalendarConnection
    {
        internal Exception? RestoreFailure;
        internal int RestoreFailuresRemaining = int.MaxValue;
        internal int Restores;
        internal int Connects;
        public CalendarService Service => service;
        public GoogleCalendarSnapshot Snapshot { get; private set; } = snapshot;
        public bool CanRefresh => true;
        public bool HasClientConfiguration => true;
        public Task<GoogleCalendarSnapshot?> RestoreAsync(CancellationToken cancellationToken)
        {
            Restores++;
            if (RestoreFailure is { } error && RestoreFailuresRemaining > 0)
            {
                RestoreFailuresRemaining--;
                return Task.FromException<GoogleCalendarSnapshot?>(error);
            }
            return Task.FromResult<GoogleCalendarSnapshot?>(Snapshot);
        }
        public Task<GoogleCalendarSnapshot> ConnectAsync(string? path, CancellationToken cancellationToken)
        { Connects++; return Task.FromResult(Snapshot); }
        public Task<GoogleCalendarSnapshot> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task SaveSelectionAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
        { Snapshot = Snapshot with { SelectedIds = ids.ToArray() }; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class FakeFactory(FakeHandler handler) : IHttpClientFactory
    {
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) => new(new ConfigurableMessageHandler(handler));
    }
    private sealed class FakeHandler(string?[] pages) : HttpMessageHandler
    {
        internal int Pages;
        internal int Defaults;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Require(request.Method == HttpMethod.Get);
            string? content;
            if (request.RequestUri.AbsolutePath.Contains("/calendarList/"))
            { Defaults++; content = "{\"defaultReminders\":[]}"; }
            else
            {
                Require(query.Contains("singleEvents=true") && query.Contains("timeMax=")
                    && !query.Contains("&q=") && !query.Contains("updatedMin"));
                content = pages[Pages++];
            }
            return Task.FromResult(new HttpResponseMessage(content is null ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            { Content = new StringContent(content ?? "{\"error\":{\"code\":400,\"message\":\"Fixture\"}}", Encoding.UTF8, "application/json") });
        }
    }
}
