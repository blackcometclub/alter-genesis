using System.IO;
using System.Text.Json;
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

    internal static async Task<ClientSecrets> ReadDesktopClientAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("installed", out var installed)
            || !installed.TryGetProperty("client_id", out var id) || id.ValueKind != JsonValueKind.String
            || !installed.TryGetProperty("client_secret", out var secret) || secret.ValueKind != JsonValueKind.String
            || !id.GetString()!.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(secret.GetString()))
            throw new InvalidDataException("A desktop OAuth client is required.");
        return new ClientSecrets { ClientId = id.GetString(), ClientSecret = secret.GetString() };
    }

    public async Task<GoogleCalendarSnapshot?> RestoreAsync(CancellationToken cancellationToken)
    {
        var client = await _store.GetAsync<ClientSecrets>("client");
        var token = await _store.GetAsync<TokenResponse>(UserKey);
        if (client is null || token is null) return null;
        await LoadSnapshotAsync();
        SetCredential(new UserCredential(CreateFlow(client), UserKey, token));
        return await RefreshAsync(cancellationToken);
    }

    public async Task<GoogleCalendarSnapshot> ConnectAsync(string? clientJsonPath, CancellationToken cancellationToken)
    {
        if (clientJsonPath is not null)
            await _store.StoreAsync("client", await ReadDesktopClientAsync(clientJsonPath, cancellationToken));
        var client = await _store.GetAsync<ClientSecrets>("client")
            ?? throw new InvalidDataException("No desktop client configuration.");
        await LoadSnapshotAsync();
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = client, Prompt = "select_account" },
            Scopes, UserKey, true, cancellationToken, _store);
        // Google can return a partial grant when the user deselects a permission.
        if (!string.IsNullOrWhiteSpace(credential.Token.Scope)
            && Scopes.Except(credential.Token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal).Any())
        {
            await _store.DeleteAsync<TokenResponse>(UserKey);
            throw new GooglePermissionException();
        }
        SetCredential(credential);
        return await RefreshAsync(cancellationToken);
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
            || saved.Calendars.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != saved.Calendars.Length)
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
