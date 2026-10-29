using System.IO;
using System.Net;
using System.Net.Http;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace AlterGenesis.NotificationProbe.Calendars;

public sealed class IcalFeedException(IcalFeedError error, Exception? innerException = null)
    : Exception(error.ToString(), innerException)
{
    public IcalFeedError Error { get; } = error;
}
public enum IcalFeedError
{
    InvalidUrl,
    ConnectionFailed,
    AccessDenied,
    TooLarge,
    InvalidCalendar,
}

public sealed class IcalFeedClient(HttpMessageHandler? handler = null) : IDisposable
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private const int MaximumOccurrences = 5000;
    private readonly HttpClient _client = new(handler ?? new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public async Task<IcalCalendarPreview> ReadAsync(string secretUrl, CancellationToken cancellationToken = default)
    {
        var calendar = await DownloadAsync(secretUrl, cancellationToken);
        return new IcalCalendarPreview(ReadName(calendar), calendar.Events.Count);
    }

    public async Task<IcalCalendarSnapshot> ReadEventsAsync(string secretUrl,
        DateTimeOffset fromUtc, DateTimeOffset throughUtc, TimeZoneInfo localTimeZone,
        CancellationToken cancellationToken = default)
    {
        if (throughUtc <= fromUtc) throw new ArgumentOutOfRangeException(nameof(throughUtc));
        var calendar = await DownloadAsync(secretUrl, cancellationToken);
        return new IcalCalendarSnapshot(ReadName(calendar),
            ExpandOccurrences(calendar, fromUtc, throughUtc, localTimeZone));
    }

    private async Task<Calendar> DownloadAsync(string secretUrl, CancellationToken cancellationToken)
    {
        Uri uri;
        try { uri = ValidateSecretUrl(secretUrl); }
        catch (ArgumentException exception) { throw new IcalFeedException(IcalFeedError.InvalidUrl, exception); }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw new IcalFeedException(IcalFeedError.AccessDenied);
            if (!response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 400)
                throw new IcalFeedException(IcalFeedError.ConnectionFailed);
            if (response.Content.Headers.ContentLength is > MaximumBytes)
                throw new IcalFeedException(IcalFeedError.TooLarge);

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var limited = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (limited.Length + read > MaximumBytes) throw new IcalFeedException(IcalFeedError.TooLarge);
                await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            limited.Position = 0;
            return Parse(limited);
        }
        catch (IcalFeedException) { throw; }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IcalFeedException(IcalFeedError.ConnectionFailed, exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new IcalFeedException(IcalFeedError.ConnectionFailed, exception);
        }
    }

    public static Uri ValidateSecretUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "calendar.google.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The value is not a Google Calendar secret iCal URL.", nameof(value));
        return uri;
    }

    private static Calendar Parse(Stream stream)
    {
        try
        {
            var calendars = CalendarCollection.Load(stream);
            var calendar = calendars.FirstOrDefault();
            if (calendar is null) throw new InvalidDataException("No calendar found.");
            return calendar;
        }
        catch (Exception exception) when (exception is not IcalFeedException)
        {
            throw new IcalFeedException(IcalFeedError.InvalidCalendar, exception);
        }
    }

    private static string ReadName(Calendar calendar)
    {
        var name = calendar.Properties.Get<string>("X-WR-CALNAME")?.Trim();
        return string.IsNullOrWhiteSpace(name) ? "Google Calendar" : name;
    }

    private static IReadOnlyList<IcalEventOccurrence> ExpandOccurrences(Calendar calendar,
        DateTimeOffset fromUtc, DateTimeOffset throughUtc, TimeZoneInfo localTimeZone)
    {
        try
        {
            var evaluationStart = new CalDateTime(fromUtc.UtcDateTime.AddDays(-1), true);
            var expanded = new List<IcalEventOccurrence>();
            foreach (var occurrence in calendar.GetOccurrences<CalendarEvent>(evaluationStart,
                         new EvaluationOptions { MaxUnmatchedIncrementsLimit = MaximumOccurrences })
                     .Take(MaximumOccurrences + 1))
            {
                if (expanded.Count == MaximumOccurrences)
                    throw new IcalFeedException(IcalFeedError.TooLarge);
                if (occurrence.Source is not CalendarEvent source
                    || string.Equals(source.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;

                var start = occurrence.Period.StartTime;
                var startUtc = ToUtc(start, localTimeZone);
                DateOnly? allDayDate = !start.HasTime ? start.Date : null;
                var boundary = allDayDate is { } date
                    ? AtLocalTime(date, TimeOnly.MinValue, localTimeZone)
                    : startUtc;
                if (boundary >= throughUtc) break;
                if (boundary < fromUtc.AddDays(-1)) continue;

                var uid = string.IsNullOrWhiteSpace(source.Uid)
                    ? $"missing:{source.Summary}:{start}"
                    : source.Uid;
                expanded.Add(new IcalEventOccurrence(uid, source.Summary ?? string.Empty,
                    startUtc, allDayDate));
            }
            return expanded;
        }
        catch (IcalFeedException) { throw; }
        catch (Exception exception)
        {
            throw new IcalFeedException(IcalFeedError.InvalidCalendar, exception);
        }
    }

    private static DateTimeOffset ToUtc(CalDateTime value, TimeZoneInfo localTimeZone)
    {
        if (!value.HasTime)
            return AtLocalTime(value.Date, TimeOnly.MinValue, localTimeZone);
        if (value.IsFloating)
        {
            var local = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, localTimeZone.GetUtcOffset(local)).ToUniversalTime();
        }
        return new DateTimeOffset(DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc));
    }

    private static DateTimeOffset AtLocalTime(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    public void Dispose() => _client.Dispose();
}
