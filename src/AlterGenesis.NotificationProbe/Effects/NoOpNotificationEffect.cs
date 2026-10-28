using AlterGenesis.NotificationProbe.Models;

namespace AlterGenesis.NotificationProbe.Effects;

internal sealed class NoOpNotificationEffect : INotificationEffect
{
    public Task OnNotificationPresentedAsync(
        IReadOnlyCollection<NotificationItem> notifications,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
