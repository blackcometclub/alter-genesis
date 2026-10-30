using System.Windows.Media;
using AlterGenesis.NotificationProbe.Models;

namespace AlterGenesis.NotificationProbe.Notifications;

internal enum NotificationStatus { Visible, Snoozed, Scheduled }

internal sealed record ScheduledNotification(NotificationItem Item, DateTimeOffset DueAtUtc, string SourceId);

internal sealed record SavedNotification(string Id, string StartTime, string Title,
    string CalendarColor, string EventUrl, NotificationStatus Status, DateTimeOffset? DueAtUtc,
    string? SourceId = null, string? StartTimeResourceKey = null, string? TitleResourceKey = null)
{
    internal static SavedNotification FromItem(NotificationItem item) => new(item.Id,
        item.StartTime, item.Title, ((SolidColorBrush)item.CalendarBrush).Color.ToString(),
        item.EventUri.AbsoluteUri, NotificationStatus.Visible, null, null,
        item.StartTimeResourceKey, item.TitleResourceKey);

    internal static SavedNotification FromScheduled(ScheduledNotification scheduled) => new(
        scheduled.Item.Id, scheduled.Item.StartTime, scheduled.Item.Title,
        ((SolidColorBrush)scheduled.Item.CalendarBrush).Color.ToString(),
        scheduled.Item.EventUri.AbsoluteUri, NotificationStatus.Scheduled,
        scheduled.DueAtUtc.ToUniversalTime(), scheduled.SourceId,
        scheduled.Item.StartTimeResourceKey, scheduled.Item.TitleResourceKey);

    internal NotificationItem ToItem()
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(CalendarColor));
        brush.Freeze();
        var calendarItem = SourceId?.StartsWith("google-", StringComparison.Ordinal) == true
            || SourceId?.StartsWith("ical-", StringComparison.Ordinal) == true;
        var startKey = StartTimeResourceKey ?? (calendarItem && (StartTime is "終日" or "All day")
            ? "Text.AllDay" : null);
        var titleKey = TitleResourceKey ?? (calendarItem && (Title is "（無題）" or "(Untitled)")
            ? "Text.UntitledEvent" : null);
        return new NotificationItem(StartTime, Title, brush, new Uri(EventUrl))
        {
            Id = Id,
            StartTimeResourceKey = startKey,
            TitleResourceKey = titleKey,
        };
    }
}
