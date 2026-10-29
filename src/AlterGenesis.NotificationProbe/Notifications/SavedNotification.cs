using System.Windows.Media;
using AlterGenesis.NotificationProbe.Models;

namespace AlterGenesis.NotificationProbe.Notifications;

internal enum NotificationStatus { Visible, Snoozed, Scheduled }

internal sealed record ScheduledNotification(NotificationItem Item, DateTimeOffset DueAtUtc, string SourceId);

internal sealed record SavedNotification(string Id, string StartTime, string Title,
    string CalendarColor, string EventUrl, NotificationStatus Status, DateTimeOffset? DueAtUtc,
    string? SourceId = null)
{
    internal static SavedNotification FromItem(NotificationItem item) => new(item.Id,
        item.StartTime, item.Title, ((SolidColorBrush)item.CalendarBrush).Color.ToString(),
        item.EventUri.AbsoluteUri, NotificationStatus.Visible, null);

    internal static SavedNotification FromScheduled(ScheduledNotification scheduled) => new(
        scheduled.Item.Id, scheduled.Item.StartTime, scheduled.Item.Title,
        ((SolidColorBrush)scheduled.Item.CalendarBrush).Color.ToString(),
        scheduled.Item.EventUri.AbsoluteUri, NotificationStatus.Scheduled,
        scheduled.DueAtUtc.ToUniversalTime(), scheduled.SourceId);

    internal NotificationItem ToItem()
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(CalendarColor));
        brush.Freeze();
        return new NotificationItem(StartTime, Title, brush, new Uri(EventUrl)) { Id = Id };
    }
}
