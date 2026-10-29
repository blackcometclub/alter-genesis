namespace AlterGenesis.NotificationProbe.Calendars;

public sealed record IcalCalendarSnapshot(
    string Name,
    IReadOnlyList<IcalEventOccurrence> Occurrences);

public sealed record IcalEventOccurrence(
    string EventUid,
    string Title,
    DateTimeOffset StartsAtUtc,
    DateOnly? AllDayDate)
{
    public bool IsAllDay => AllDayDate is not null;
}
