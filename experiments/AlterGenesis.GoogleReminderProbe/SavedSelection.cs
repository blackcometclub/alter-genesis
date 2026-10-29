namespace AlterGenesis.NotificationProbe.Google;

// Keep the preserved experiment's type name: ProtectedGoogleStore hashes it into the filename.
public sealed record GoogleCalendarDescriptor(string Id, string Name, string Color, bool Primary);
public sealed record GoogleCalendarSnapshot(int Version, GoogleCalendarDescriptor[] Calendars, string[] SelectedIds);
