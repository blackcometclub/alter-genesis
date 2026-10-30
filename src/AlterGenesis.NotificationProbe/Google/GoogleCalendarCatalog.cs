using System.IO;
using Google.Apis.Calendar.v3;

namespace AlterGenesis.NotificationProbe.Google;

public sealed record GoogleCalendarDescriptor(string Id, string Name, string Color, bool Primary);
public sealed record GoogleCalendarSnapshot(int Version, GoogleCalendarDescriptor[] Calendars, string[] SelectedIds)
{
    public static GoogleCalendarSnapshot Empty => new(1, [], []);
}

public interface IGoogleCalendarConnection
{
    CalendarService Service { get; }
    GoogleCalendarSnapshot Snapshot { get; }
    bool CanRefresh { get; }
    bool HasClientConfiguration { get; }
    Task<GoogleCalendarSnapshot?> RestoreAsync(CancellationToken cancellationToken);
    Task<GoogleCalendarSnapshot> ConnectAsync(string? clientJsonPath, CancellationToken cancellationToken);
    Task<GoogleCalendarSnapshot> RefreshAsync(CancellationToken cancellationToken);
    Task SaveSelectionAsync(IReadOnlyCollection<string> selectedIds, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

internal static class GoogleCalendarCatalog
{
    internal static async Task<GoogleCalendarDescriptor[]> FetchAsync(CalendarService service, CancellationToken cancellationToken)
    {
        var calendars = new Dictionary<string, GoogleCalendarDescriptor>(StringComparer.Ordinal);
        var pageTokens = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;
        do
        {
            var request = service.CalendarList.List();
            request.MaxResults = 250;
            request.PageToken = page;
            request.Fields = "items(id,summary,summaryOverride,backgroundColor,primary,accessRole),nextPageToken";
            var response = await request.ExecuteAsync(cancellationToken);
            foreach (var item in response.Items ?? [])
            {
                if (string.IsNullOrWhiteSpace(item.Id) || item.AccessRole is "freeBusyReader" or "none") continue;
                calendars[item.Id] = new(item.Id, item.SummaryOverride ?? item.Summary ?? "",
                    item.BackgroundColor ?? "#71859C", item.Primary == true);
            }
            page = response.NextPageToken;
            if (pageTokens.Count >= 100 || calendars.Count > 5000)
                throw new InvalidDataException("Calendar catalog limit exceeded.");
            if (!string.IsNullOrEmpty(page) && !pageTokens.Add(page))
                throw new InvalidDataException("Repeated calendar page token.");
        } while (!string.IsNullOrEmpty(page));
        return calendars.Values.OrderByDescending(item => item.Primary)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
