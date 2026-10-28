using System.Windows.Media;
using AlterGenesis.NotificationProbe.Models;

namespace AlterGenesis.NotificationProbe.Notifications;

internal enum NotificationStatus { Visible, Snoozed }

internal sealed record SavedNotification(string Id, string StartTime, string Title,
    string CalendarColor, string EventUrl, NotificationStatus Status, DateTimeOffset? DueAtUtc)
{
    internal static SavedNotification FromItem(NotificationItem item) => new(item.Id,
        item.StartTime, item.Title, ((SolidColorBrush)item.CalendarBrush).Color.ToString(),
        item.EventUri.AbsoluteUri, NotificationStatus.Visible, null);

    internal NotificationItem ToItem()
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(CalendarColor));
        brush.Freeze();
        return new NotificationItem(StartTime, Title, brush, new Uri(EventUrl)) { Id = Id };
    }
}
