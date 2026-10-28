using System.Windows.Media;

namespace AlterGenesis.NotificationProbe.Models;

public sealed record NotificationItem(
    string StartTime,
    string Title,
    Brush CalendarBrush,
    Uri EventUri)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}
