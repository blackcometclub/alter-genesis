using System.IO;
using System.Security.Cryptography;
using AlterGenesis.NotificationProbe.Models;
using AlterGenesis.NotificationProbe.Notifications;
using Google.Apis.Auth.OAuth2.Responses;

namespace AlterGenesis.NotificationProbe.Google;

// One owner for startup and settings operations. UI closure does not dispose shared credentials.
internal sealed class GoogleNotificationSession(IGoogleCalendarConnection connection,
    NotificationSchedule schedule, Action<IReadOnlyCollection<NotificationItem>> publish,
    Func<string> untitled, Func<TimeSpan, CancellationToken, Task>? wait = null,
    Func<string>? allDay = null) : IDisposable
{
    private static readonly TimeSpan[] AccessDeniedRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)];
    private static readonly TimeSpan[] TransientRetryDelays = [TimeSpan.FromSeconds(2)];
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait ?? Task.Delay;
    internal GoogleCalendarSnapshot Snapshot { get; private set; } = GoogleCalendarSnapshot.Empty;
    internal bool Connected { get; private set; }
    internal bool Busy { get; private set; }
    internal bool ScheduleBlocked { get; private set; }
    internal string? StatusKey { get; private set; }
    internal string? FailureCode { get; private set; }
    internal event Action? Changed;
    private CancellationTokenSource? _operation;
    private bool _disposed;

    internal Task RestoreAsync() => RunAsync(async token =>
    {
        var restored = await RestoreWithStartupRetriesAsync(token);
        if (restored is null) return;
        Snapshot = restored;
        Connected = true;
        await SynchronizeAsync(token);
    });

    private async Task<GoogleCalendarSnapshot?> RestoreWithStartupRetriesAsync(CancellationToken token)
    {
        var attempt = 0;
        while (true)
        {
            try { return await connection.RestoreAsync(token); }
            catch (System.Net.Http.HttpRequestException exception)
            {
                // A firewall may reject the first socket immediately while its approval prompt is open.
                // Keep the UI responsive and give that human approval a bounded window to complete.
                var delays = HasSocketError(exception, System.Net.Sockets.SocketError.AccessDenied)
                    ? AccessDeniedRetryDelays : TransientRetryDelays;
                if (attempt >= delays.Length) throw;
                await _wait(delays[attempt++], token);
            }
        }
    }

    private static bool HasSocketError(Exception exception, System.Net.Sockets.SocketError expected)
    {
        for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
            if (inner is System.Net.Sockets.SocketException socket && socket.SocketErrorCode == expected) return true;
        return false;
    }

    internal Task ConnectAsync() => RunAsync(async token =>
    {
        Snapshot = await connection.ConnectAsync(null, token);
        Connected = true;
        await SynchronizeAsync(token);
    }, authorization: true);

    internal Task RefreshAsync() => RunAsync(async token =>
    {
        Snapshot = await connection.RefreshAsync(token);
        Connected = true;
        await SynchronizeAsync(token);
    });

    internal Task SelectAsync(string[] ids) => RunAsync(async token =>
    {
        await connection.SaveSelectionAsync(ids, token);
        Snapshot = connection.Snapshot;
        await SynchronizeAsync(token);
    });

    internal Task DisconnectAsync() => RunAsync(async token =>
    {
        if (!schedule.TryKeepCalendarSources(new HashSet<string>())) throw new GoogleNotificationSaveException();
        publish([]);
        await connection.DisconnectAsync(token);
        Connected = false;
        Snapshot = GoogleCalendarSnapshot.Empty;
    });

    internal void Cancel() => _operation?.Cancel();

    private async Task SynchronizeAsync(CancellationToken token)
    {
        var sources = Snapshot.SelectedIds.Select(GoogleNotificationReader.SourceId).ToHashSet(StringComparer.Ordinal);
        // Unchecking immediately disables that calendar, even if fetching another calendar later fails.
        if (!schedule.TryKeepCalendarSources(sources)) throw new GoogleNotificationSaveException();
        publish([]);
        var read = await GoogleNotificationReader.ReadAsync(connection.Service, Snapshot, schedule.Items,
            DateTimeOffset.UtcNow, TimeZoneInfo.Local, untitled(), token, allDay?.Invoke() ?? "All day");
        token.ThrowIfCancellationRequested();
        if (!schedule.TryReplaceCalendarSchedules(read.Items, sources)) throw new GoogleNotificationSaveException();
        ScheduleBlocked = false;
        StatusKey = "Text.CalendarEventsRefreshSucceeded";
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, bool authorization = false)
    {
        if (Busy || _disposed) return;
        using var operation = new CancellationTokenSource(authorization ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(2));
        _operation = operation;
        Busy = true;
        FailureCode = null;
        StatusKey = authorization ? "Text.GoogleAuthorizing" : "Text.CalendarEventsRefreshing";
        Changed?.Invoke();
        try { await work(operation.Token); }
        catch (OperationCanceledException) { StatusKey = "Text.GoogleCanceled"; }
        catch (TokenResponseException exception) when (exception.Error?.Error == "invalid_grant")
        { Connected = false; StatusKey = "Text.GoogleReconnect"; }
        catch (GooglePermissionException) { Connected = false; StatusKey = "Text.GooglePermissionsRequired"; }
        catch (GoogleNotificationSaveException) { ScheduleBlocked = true; StatusKey = "Text.NotificationsSaveFailed"; }
        catch (CryptographicException) { StatusKey = "Text.GoogleCredentialsUnreadable"; }
        catch (InvalidDataException) { StatusKey = "Text.GoogleConfigurationInvalid"; }
        catch (Newtonsoft.Json.JsonException) { StatusKey = "Text.GoogleCredentialsUnreadable"; }
        catch (Exception exception)
        {
            if (connection.CanRefresh)
            {
                Snapshot = connection.Snapshot;
                Connected = true;
            }
            StatusKey = "Text.GoogleConnectionFailed";
            if (Connected)
            {
                // A selection may have saved before a previous notification-file write failed.
                // Honor it after restart even when the network cannot refresh the catalog.
                var selected = Snapshot.SelectedIds.Select(GoogleNotificationReader.SourceId).ToHashSet(StringComparer.Ordinal);
                if (!schedule.TryKeepCalendarSources(selected))
                {
                    ScheduleBlocked = true;
                    StatusKey = "Text.NotificationsSaveFailed";
                }
            }
            // Never include exception messages, URLs, account names or credential values in diagnostics.
            FailureCode = exception is global::Google.GoogleApiException api
                ? $"GoogleHttp{(int)api.HttpStatusCode}" : exception is System.Net.Http.HttpRequestException http
                    ? TransportFailureCode(http) : exception.GetType().Name;
        }
        finally
        {
            Busy = false;
            _operation = null;
            // Failed downloads retain the last complete schedule; never replace it with a partial page.
            if (!_disposed)
            {
                IReadOnlyList<NotificationItem> presented = [];
                if (!ScheduleBlocked && !schedule.TryReleaseDue(out presented)) StatusKey = "Text.NotificationsSaveFailed";
                publish(presented);
                if (!Connected && StatusKey == "Text.CalendarEventsRefreshing") StatusKey = null;
                Changed?.Invoke();
            }
        }
    }

    private static string TransportFailureCode(System.Net.Http.HttpRequestException exception)
    {
        // Error categories identify transport failures without exposing URLs or token-bearing messages.
        var code = $"HttpRequest:{exception.HttpRequestError}";
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            if (inner is System.Net.Sockets.SocketException socket)
                return $"{code}:{socket.SocketErrorCode}";
        return code;
    }

    public void Dispose() { _disposed = true; Cancel(); (connection as IDisposable)?.Dispose(); }
}

internal sealed class GoogleNotificationSaveException : Exception;
