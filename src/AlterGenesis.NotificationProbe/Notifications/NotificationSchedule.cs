using AlterGenesis.NotificationProbe.Models;

namespace AlterGenesis.NotificationProbe.Notifications;

// Commit the next snapshot before changing anything that the user can see.
internal sealed class NotificationSchedule(INotificationStore store,
    IReadOnlyList<SavedNotification> initial, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private IReadOnlyList<SavedNotification> _items = initial.ToArray();
    internal IReadOnlyList<SavedNotification> Items => _items;
    internal IReadOnlyList<NotificationItem> Visible => _items
        .Where(item => item.Status == NotificationStatus.Visible).Select(item => item.ToItem()).ToArray();
    internal bool HasPending => _items.Any(item => item.Status is NotificationStatus.Snoozed
        or NotificationStatus.Scheduled);

    internal bool TryAdd(IReadOnlyCollection<NotificationItem> items) =>
        Commit(_items.Concat(items.Select(SavedNotification.FromItem)).ToArray());

    internal bool TryReplaceCalendarSchedule(string sourceId,
        IReadOnlyCollection<ScheduledNotification> scheduled)
    {
        if (string.IsNullOrWhiteSpace(sourceId)
            || scheduled.Any(item => item.SourceId != sourceId)) return false;
        var retained = _items.Where(item => item.Status != NotificationStatus.Scheduled
            || item.SourceId != sourceId).ToArray();
        var retainedIds = retained.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var additions = scheduled.Where(item => !retainedIds.Contains(item.Item.Id))
            .Select(SavedNotification.FromScheduled);
        return Commit(retained.Concat(additions).ToArray());
    }

    internal bool TrySnooze(string id, int minutes)
    {
        if (minutes is not (5 or 10 or 30 or 60)
            || !_items.Any(item => item.Id == id && item.Status == NotificationStatus.Visible)) return false;
        var due = _clock().ToUniversalTime().AddMinutes(minutes);
        return Commit(_items.Select(item => item.Id == id
            ? item with { Status = NotificationStatus.Snoozed, DueAtUtc = due } : item).ToArray());
    }

    internal bool TryKeepCalendarSources(IReadOnlySet<string> sources) =>
        Commit(_items.Where(item => item.SourceId is null || sources.Contains(item.SourceId)).ToArray());

    internal bool TryReplaceCalendarSchedules(IReadOnlyCollection<ScheduledNotification> scheduled,
        IReadOnlySet<string> selectedSources)
    {
        if (scheduled.Any(item => !selectedSources.Contains(item.SourceId))) return false;
        var retained = _items.Where(item => item.SourceId is null
            || selectedSources.Contains(item.SourceId) && item.Status != NotificationStatus.Scheduled).ToArray();
        var ids = retained.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return Commit(retained.Concat(scheduled.Where(item => !ids.Contains(item.Item.Id))
            .Select(SavedNotification.FromScheduled)).ToArray());
    }

    internal bool TryDismiss(string id) =>
        _items.Any(item => item.Id == id && item.Status == NotificationStatus.Visible)
        && Commit(_items.Where(item => item.Id != id).ToArray());

    internal bool TryReleaseDue(out IReadOnlyList<NotificationItem> presented)
    {
        var now = _clock().ToUniversalTime();
        var due = _items.Where(item => (item.Status is NotificationStatus.Snoozed
                or NotificationStatus.Scheduled) && item.DueAtUtc <= now).ToArray();
        presented = [];
        if (due.Length == 0) return true;
        var ids = due.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (!Commit(_items.Select(item => ids.Contains(item.Id)
            ? item with { Status = NotificationStatus.Visible, DueAtUtc = null } : item).ToArray())) return false;
        presented = due.Select(item => item.ToItem()).ToArray();
        return true;
    }

    private bool Commit(IReadOnlyList<SavedNotification> next)
    {
        if (!store.TrySave(next)) return false;
        _items = next;
        return true;
    }
}
