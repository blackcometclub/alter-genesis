namespace AlterGenesis.NotificationProbe.Google;

// Tracks only cadence. GoogleNotificationSession remains the single owner of refresh work and failures.
internal sealed class GooglePeriodicRefresh(Func<DateTimeOffset>? clock = null, TimeSpan? interval = null)
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly TimeSpan _interval = interval ?? DefaultInterval;
    private DateTimeOffset? _nextDueUtc;
    private bool _waitingForCompletion;

    internal DateTimeOffset? NextDueUtc => _nextDueUtc;
    internal bool IsActive => _nextDueUtc is not null || _waitingForCompletion;

    internal void Update(bool connected, bool busy)
    {
        if (!connected)
        {
            _nextDueUtc = null;
            _waitingForCompletion = false;
            return;
        }

        if (busy)
        {
            _nextDueUtc = null;
            _waitingForCompletion = true;
            return;
        }

        if (_waitingForCompletion || _nextDueUtc is null)
        {
            _waitingForCompletion = false;
            _nextDueUtc = _clock().ToUniversalTime().Add(_interval);
        }
    }

    internal bool TryTakeDue(bool connected, bool busy)
    {
        Update(connected, busy);
        if (_nextDueUtc is not { } due || _clock().ToUniversalTime() < due) return false;
        _nextDueUtc = null;
        _waitingForCompletion = true;
        return true;
    }
}
