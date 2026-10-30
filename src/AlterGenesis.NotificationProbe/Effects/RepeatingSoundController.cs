using System.Diagnostics;
using AlterGenesis.NotificationProbe.Settings;

namespace AlterGenesis.NotificationProbe.Effects;

// Owns only the next repeat deadline. Notification visibility remains owned by NotificationSchedule.
internal sealed class RepeatingSoundController(INotificationSoundPlayer player,
    Func<AppSettings> selectedSettings, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private bool _hasVisibleNotifications;
    private DateTimeOffset? _nextDueUtc;

    internal bool IsActive => _nextDueUtc is not null;
    internal DateTimeOffset? NextDueUtc => _nextDueUtc;

    internal void Update(bool hasVisibleNotifications, bool resetCountdown = false)
    {
        var becameVisible = hasVisibleNotifications && !_hasVisibleNotifications;
        _hasVisibleNotifications = hasVisibleNotifications;
        var settings = selectedSettings();
        var delay = settings.SoundRepeat.Delay();
        if (!hasVisibleNotifications || settings.Sound == NotificationSound.None || delay is null)
        {
            _nextDueUtc = null;
            return;
        }

        if (becameVisible || resetCountdown || _nextDueUtc is null)
            _nextDueUtc = _clock().ToUniversalTime().Add(delay.Value);
    }

    internal void Tick(bool hasVisibleNotifications)
    {
        Update(hasVisibleNotifications);
        if (_nextDueUtc is not { } due || _clock().ToUniversalTime() < due) return;

        var settings = selectedSettings();
        try
        {
            if (settings.Sound != NotificationSound.None && !player.TryPlay(settings.Sound))
                Trace.TraceWarning("Repeated notification sound could not be played.");
        }
        catch (Exception exception)
        {
            // Repeating sound is optional and must never dismiss or block a notification.
            Trace.TraceWarning("Repeated notification sound failed: {0}", exception.Message);
        }

        var delay = settings.SoundRepeat.Delay();
        _nextDueUtc = _hasVisibleNotifications && settings.Sound != NotificationSound.None && delay is not null
            ? _clock().ToUniversalTime().Add(delay.Value) : null;
    }
}
