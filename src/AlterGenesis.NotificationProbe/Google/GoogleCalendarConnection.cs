using System.IO;
using System.Reflection;
using Google.Apis.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Services;

namespace AlterGenesis.NotificationProbe.Google;

internal sealed class GoogleCalendarConnection(string directory) : IGoogleCalendarConnection, IDisposable
{
    internal static readonly string[] Scopes =
    ["https://www.googleapis.com/auth/calendar.calendarlist.readonly", "https://www.googleapis.com/auth/calendar.events.readonly"];
    private const string UserKey = "calendar-user";
    private readonly ProtectedGoogleStore _store = new(Path.Combine(directory, "google"));
    private GoogleAuthorizationCodeFlow? _flow;
    private UserCredential? _credential;
    private CalendarService? _service;
    private GoogleCalendarSnapshot _snapshot = GoogleCalendarSnapshot.Empty;
    public bool HasClientConfiguration => _store.Contains<ClientSecrets>("client");
    public CalendarService Service => _service ?? throw new InvalidOperationException("Not connected.");
    public GoogleCalendarSnapshot Snapshot => _snapshot;
    public bool CanRefresh => _service is not null;

    private async Task<ClientSecrets> ReadClientAsync(CancellationToken cancellationToken)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AlterGenesis.Google.Client")
            ?? throw new InvalidDataException("Developer client configuration is not embedded.");
        using var reader = new StreamReader(stream);
        var client = NewtonsoftJsonSerializer.Instance.Deserialize<ClientSecrets>(await reader.ReadToEndAsync(cancellationToken));
        if (client is null || string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.ClientSecret)
            || !client.ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid developer client configuration.");
        var saved = await _store.GetAsync<ClientSecrets>("client");
        if (saved is not null && saved.ClientId != client.ClientId)
            throw new InvalidDataException("Client configuration changed; existing credentials are preserved.");
        if (saved is null) await _store.StoreAsync("client", client);
        return client;
    }

    public async Task<GoogleCalendarSnapshot?> RestoreAsync(CancellationToken cancellationToken)
    {
        var token = await _store.GetAsync<TokenResponse>(UserKey);
        if (token is null) return null;
        var client = await ReadClientAsync(cancellationToken);
        await LoadSnapshotAsync();
        var credential = new UserCredential(CreateFlow(client), UserKey, token);
        try { ValidateScopes(credential); }
        catch { credential.Flow.Dispose(); throw; }
        SetCredential(credential);
        await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        return await RefreshAsync(cancellationToken);
    }

    public async Task<GoogleCalendarSnapshot> ConnectAsync(string? clientJsonPath, CancellationToken cancellationToken)
    {
        var client = await ReadClientAsync(cancellationToken);
        await LoadSnapshotAsync();
        // Interactive reconnect is the only path allowed to discard an expired grant.
        var savedToken = await _store.GetAsync<TokenResponse>(UserKey);
        if (savedToken is not null)
        {
            using var flow = CreateFlow(client);
            try
            {
                var savedCredential = new UserCredential(flow, UserKey, savedToken);
                await savedCredential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
                ValidateScopes(savedCredential);
            }
            catch (TokenResponseException exception) when (exception.Error?.Error == "invalid_grant")
            { await _store.DeleteAsync<TokenResponse>(UserKey); }
            catch (GooglePermissionException) { await _store.DeleteAsync<TokenResponse>(UserKey); }
        }
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = client, Prompt = "select_account" },
            Scopes, UserKey, true, cancellationToken, _store);
        try { ValidateScopes(credential); }
        catch { credential.Flow.Dispose(); throw; }
        SetCredential(credential);
        return await RefreshAsync(cancellationToken);
    }

    private static void ValidateScopes(UserCredential credential)
    {
        if (!string.IsNullOrWhiteSpace(credential.Token.Scope)
            && Scopes.Except(credential.Token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal).Any())
        {
            throw new GooglePermissionException();
        }
    }

    private GoogleAuthorizationCodeFlow CreateFlow(ClientSecrets client) => new(new GoogleAuthorizationCodeFlow.Initializer
    { ClientSecrets = client, Scopes = Scopes, DataStore = _store });

    private void SetCredential(UserCredential credential)
    {
        _service?.Dispose();
        _flow?.Dispose();
        _credential = credential;
        _flow = (GoogleAuthorizationCodeFlow)credential.Flow;
        _service = new CalendarService(new BaseClientService.Initializer
        { HttpClientInitializer = credential, ApplicationName = "ALTER GENESIS" });
        _service.HttpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<GoogleCalendarSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        var calendars = await GoogleCalendarCatalog.FetchAsync(_service
            ?? throw new InvalidOperationException("Not connected."), cancellationToken);
        var available = calendars.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var next = new GoogleCalendarSnapshot(1, calendars, _snapshot.SelectedIds.Where(available.Contains).ToArray());
        await _store.StoreAsync("catalog", next);
        _snapshot = next;
        return next;
    }

    public async Task SaveSelectionAsync(IReadOnlyCollection<string> selectedIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var available = _snapshot.Calendars.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Any(id => !available.Contains(id))) throw new InvalidDataException("Unknown calendar selection.");
        var next = _snapshot with { SelectedIds = selectedIds.Distinct(StringComparer.Ordinal).ToArray() };
        await _store.StoreAsync("catalog", next);
        _snapshot = next;
    }

    private async Task LoadSnapshotAsync()
    {
        var saved = await _store.GetAsync<GoogleCalendarSnapshot>("catalog");
        if (saved is null) return;
        if (saved is not { Version: 1, Calendars: not null, SelectedIds: not null }
            || saved.Calendars.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id))
            || saved.Calendars.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != saved.Calendars.Length
            || saved.SelectedIds.Any(id => !saved.Calendars.Any(item => item.Id == id)))
            throw new InvalidDataException("Invalid calendar preferences.");
        _snapshot = saved;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_credential is not null && !await _credential.RevokeTokenAsync(cancellationToken))
            throw new InvalidOperationException("Google did not confirm revocation.");
        await _store.DeleteAsync<TokenResponse>(UserKey);
        await _store.DeleteAsync<GoogleCalendarSnapshot>("catalog");
        _snapshot = GoogleCalendarSnapshot.Empty;
        Dispose();
    }

    public void Dispose()
    {
        _service?.Dispose();
        _flow?.Dispose();
        _service = null;
        _flow = null;
        _credential = null;
    }
}

internal sealed class GooglePermissionException : Exception;
