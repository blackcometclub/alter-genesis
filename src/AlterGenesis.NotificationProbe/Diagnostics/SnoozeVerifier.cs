using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlterGenesis.NotificationProbe.Effects;
using AlterGenesis.NotificationProbe.Models;
using AlterGenesis.NotificationProbe.Notifications;

namespace AlterGenesis.NotificationProbe.Diagnostics;

internal static class SnoozeVerifier
{
    internal static bool Run(string path)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var fixtures = Path.Combine(root, "snooze-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var checks = new List<object>();
        var passed = true;
        void Check(string name, Action action)
        {
            try { action(); checks.Add(new { Name = name, Passed = true }); }
            catch (Exception exception)
            {
                passed = false;
                checks.Add(new { Name = name, Passed = false, Error = exception.ToString() });
            }
        }
        var baseline = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(9));
        foreach (var minutes in new[] { 5, 10, 30, 60 })
        {
            Check($"Preset {minutes} minutes hides only its card and stores an exact UTC deadline", () =>
            {
                var schedule = new NotificationSchedule(new MemoryStore(), [], () => baseline);
                var items = SampleNotifications.Create([]);
                Require(schedule.TryAdd(items) && schedule.TrySnooze(items[0].Id, minutes));
                Require(schedule.Visible.Count == 2 && schedule.HasPending);
                var pending = schedule.Items.Single(item => item.Id == items[0].Id);
                Require(pending.DueAtUtc == baseline.AddMinutes(minutes) && pending.DueAtUtc.Value.Offset == TimeSpan.Zero);
            });
        }
        Check("No early presentation, one batch at deadline, no repeated presentation", () =>
        {
            var now = baseline;
            var schedule = new NotificationSchedule(new MemoryStore(), [], () => now);
            var items = SampleNotifications.Create([]);
            Require(schedule.TryAdd(items));
            Require(schedule.TrySnooze(items[0].Id, 5) && schedule.TrySnooze(items[1].Id, 5));
            now = baseline.AddMinutes(5).AddTicks(-1);
            Require(schedule.TryReleaseDue(out var early) && early.Count == 0);
            now = baseline.AddMinutes(5);
            Require(schedule.TryReleaseDue(out var due) && due.Count == 2);
            var player = new CountingPlayer();
            new SoundNotificationEffect(player, () => NotificationSound.Koron)
                .OnNotificationPresentedAsync(due).GetAwaiter().GetResult();
            Require(player.Count == 1 && schedule.Visible.Count == 3);
            Require(schedule.TryReleaseDue(out var repeated) && repeated.Count == 0);
        });
        Check("Returned cards can be snoozed again; dismissed cards never return", () =>
        {
            var now = baseline;
            var schedule = new NotificationSchedule(new MemoryStore(), [], () => now);
            var item = SampleNotifications.Create(["--single"])[0];
            Require(schedule.TryAdd([item]) && schedule.TrySnooze(item.Id, 5));
            now = now.AddMinutes(5);
            Require(schedule.TryReleaseDue(out var due) && due.Single().Id == item.Id);
            Require(schedule.TrySnooze(item.Id, 10));
            now = now.AddMinutes(10);
            Require(schedule.TryReleaseDue(out due) && due.Single().Id == item.Id);
            Require(schedule.TryDismiss(item.Id));
            now = now.AddDays(1);
            Require(schedule.TryReleaseDue(out due) && due.Count == 0 && schedule.Items.Count == 0);
        });
        Check("Save failure leaves cards visible, then preserves due reservations until retry succeeds", () =>
        {
            var now = baseline;
            var store = new MemoryStore();
            var schedule = new NotificationSchedule(store, [], () => now);
            var item = SampleNotifications.Create(["--single"])[0];
            Require(schedule.TryAdd([item]));
            store.AcceptWrites = false;
            Require(!schedule.TrySnooze(item.Id, 5) && !schedule.TryDismiss(item.Id));
            Require(schedule.Visible.Single().Id == item.Id && !schedule.HasPending);
            store.AcceptWrites = true;
            Require(schedule.TrySnooze(item.Id, 5));
            now = now.AddMinutes(5);
            store.AcceptWrites = false;
            Require(!schedule.TryReleaseDue(out var due) && due.Count == 0 && schedule.HasPending);
            store.AcceptWrites = true;
            Require(schedule.TryReleaseDue(out due) && due.Single().Id == item.Id && !schedule.HasPending);
        });
        Check("Restart before and after deadline preserves card details and dismissed state", () =>
        {
            var directory = Path.Combine(fixtures, "restart");
            var store = new NotificationStore(directory);
            var now = baseline;
            var schedule = new NotificationSchedule(store, store.Load(), () => now);
            var items = SampleNotifications.Create([]);
            Require(schedule.TryAdd(items) && schedule.TrySnooze(items[0].Id, 5) && schedule.TryDismiss(items[1].Id));
            now = baseline.AddMinutes(4);
            var reopenedStore = new NotificationStore(directory);
            var reopened = new NotificationSchedule(reopenedStore, reopenedStore.Load(), () => now);
            Require(reopened.TryReleaseDue(out var due) && due.Count == 0 && reopened.Visible.Count == 1);
            now = baseline.AddMinutes(6);
            Require(reopened.TryReleaseDue(out due) && due.Single().Id == items[0].Id);
            var returned = due.Single();
            Require(returned.Title == items[0].Title && returned.StartTime == items[0].StartTime
                && returned.CalendarBrush.ToString() == items[0].CalendarBrush.ToString() && returned.EventUri == items[0].EventUri);
            Require(reopened.Items.All(item => item.Id != items[1].Id));
        });
        Check("Adding test cards preserves existing reservations; empty saved state stays empty", () =>
        {
            var store = new NotificationStore(Path.Combine(fixtures, "append"));
            var schedule = new NotificationSchedule(store, store.Load(), () => baseline);
            var item = SampleNotifications.Create(["--single"])[0];
            Require(schedule.TryAdd([item]) && schedule.TrySnooze(item.Id, 5));
            Require(schedule.TryAdd(SampleNotifications.Create([])) && schedule.HasPending && schedule.Visible.Count == 3);
            Require(store.TrySave([]));
            var reopen = new NotificationStore(Path.Combine(fixtures, "append"));
            Require(reopen.Load().Count == 0 && reopen.HasFile);
        });
        Check("Locked notification file preserves data and removes temporary files", () =>
        {
            var directory = Path.Combine(fixtures, "locked");
            var store = new NotificationStore(directory);
            var schedule = new NotificationSchedule(store, [], () => baseline);
            var item = SampleNotifications.Create(["--single"])[0];
            Require(schedule.TryAdd([item]));
            var original = File.ReadAllText(store.FilePath);
            using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Require(!schedule.TrySnooze(item.Id, 5));
            Require(File.ReadAllText(store.FilePath) == original && schedule.Visible.Count == 1);
            Require(Directory.GetFiles(directory).Length == 1);
        });
        var valid = SavedNotification.FromItem(SampleNotifications.Create(["--single"])[0]);
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        string Encode(params SavedNotification[] items) => JsonSerializer.Serialize(new { Version = 1, Items = items }, options);
        var invalidData = new[] { "{broken", "null", "{\"Version\":2,\"Items\":[]}", "{\"Version\":1,\"Items\":null}",
            Encode(valid, valid), Encode(valid with { Status = NotificationStatus.Snoozed }),
            Encode(valid with { DueAtUtc = baseline }), Encode(valid with { EventUrl = "file:///test.exe" }),
            Encode(valid with { Status = NotificationStatus.Scheduled, DueAtUtc = baseline, SourceId = null }) };
        for (var index = 0; index < invalidData.Length; index++)
        {
            var caseIndex = index;
            Check($"Invalid notification data {index} cannot be overwritten", () =>
            {
                var directory = Path.Combine(fixtures, "invalid-" + caseIndex);
                Directory.CreateDirectory(directory);
                var store = new NotificationStore(directory);
                File.WriteAllText(store.FilePath, invalidData[caseIndex]);
                Require(store.Load().Count == 0 && store.LoadFailed && !store.TrySave([]));
                Require(File.ReadAllText(store.FilePath) == invalidData[caseIndex]);
            });
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new { Passed = passed, Fixtures = fixtures, Checks = checks },
            new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Check failed.");
    }
    private sealed class MemoryStore : INotificationStore
    {
        internal bool AcceptWrites = true;
        public bool TrySave(IReadOnlyList<SavedNotification> items) => AcceptWrites;
    }
    private sealed class CountingPlayer : INotificationSoundPlayer
    {
        internal int Count;
        public bool TryPlay(NotificationSound sound) { Count++; return true; }
        public void Stop() { }
    }
}
