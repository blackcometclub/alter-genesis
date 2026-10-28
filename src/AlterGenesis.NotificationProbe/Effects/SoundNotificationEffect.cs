using System.Diagnostics;
using AlterGenesis.NotificationProbe.Models;

namespace AlterGenesis.NotificationProbe.Effects;

public sealed class SoundNotificationEffect(
    INotificationSoundPlayer player,
    Func<NotificationSound> selectedSound) : INotificationEffect
{
    public Task OnNotificationPresentedAsync(
        IReadOnlyCollection<NotificationItem> notifications,
        CancellationToken cancellationToken = default)
    {
        if (notifications.Count == 0 || cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;

        try
        {
            var sound = selectedSound();
            if (sound != NotificationSound.None && !player.TryPlay(sound))
                Trace.TraceWarning("Notification sound could not be played.");
        }
        catch (Exception exception)
        {
            // Optional effects must never dismiss or prevent the notification panel.
            Trace.TraceWarning("Notification sound failed: {0}", exception.Message);
        }

        return Task.CompletedTask;
    }
}
