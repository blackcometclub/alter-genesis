namespace AlterGenesis.NotificationProbe.Effects;

public enum NotificationSoundRepeat
{
    None,
    OneMinute,
    ThreeMinutes,
    FiveMinutes,
    TenMinutes,
}

internal static class NotificationSoundRepeatExtensions
{
    internal static TimeSpan? Delay(this NotificationSoundRepeat repeat) => repeat switch
    {
        NotificationSoundRepeat.None => null,
        NotificationSoundRepeat.OneMinute => TimeSpan.FromMinutes(1),
        NotificationSoundRepeat.ThreeMinutes => TimeSpan.FromMinutes(3),
        NotificationSoundRepeat.FiveMinutes => TimeSpan.FromMinutes(5),
        NotificationSoundRepeat.TenMinutes => TimeSpan.FromMinutes(10),
        _ => null,
    };
}
