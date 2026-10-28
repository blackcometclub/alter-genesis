using AlterGenesis.NotificationProbe.Models;

namespace AlterGenesis.NotificationProbe.Effects;

public interface INotificationEffect
{
    Task OnNotificationPresentedAsync(
        IReadOnlyCollection<NotificationItem> notifications,
        CancellationToken cancellationToken = default);
}
