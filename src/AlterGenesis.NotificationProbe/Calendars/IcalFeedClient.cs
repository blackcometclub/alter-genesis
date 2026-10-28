using System.IO;
using System.Net;
using System.Net.Http;
using Ical.Net;

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
        Uri uri;
        try { uri = ValidateSecretUrl(secretUrl); }
        catch (ArgumentException exception) { throw new IcalFeedException(IcalFeedError.InvalidUrl, exception); }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw new IcalFeedException(IcalFeedError.AccessDenied);
            if (!response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 400)
                throw new IcalFeedException(IcalFeedError.ConnectionFailed);
            if (response.Content.Headers.ContentLength is > MaximumBytes)
                throw new IcalFeedException(IcalFeedError.TooLarge);

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var limited = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (limited.Length + read > MaximumBytes) throw new IcalFeedException(IcalFeedError.TooLarge);
                await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
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

    private static IcalCalendarPreview Parse(Stream stream)
    {
        try
        {
            var calendars = CalendarCollection.Load(stream);
            var calendar = calendars.FirstOrDefault();
            if (calendar is null) throw new InvalidDataException("No calendar found.");
            var name = calendar.Properties.Get<string>("X-WR-CALNAME")?.Trim();
            return new IcalCalendarPreview(string.IsNullOrWhiteSpace(name) ? "Google Calendar" : name,
                calendar.Events.Count);
        }
        catch (Exception exception) when (exception is not IcalFeedException)
        {
            throw new IcalFeedException(IcalFeedError.InvalidCalendar, exception);
        }
    }

    public void Dispose() => _client.Dispose();
}
