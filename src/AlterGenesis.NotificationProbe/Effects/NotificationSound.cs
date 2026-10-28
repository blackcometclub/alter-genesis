namespace AlterGenesis.NotificationProbe.Effects;

public enum NotificationSound
{
    None,
    Koron,
    Kirari,
    Pikon,
}

public interface INotificationSoundPlayer
{
    bool TryPlay(NotificationSound sound);
    void Stop();
}
