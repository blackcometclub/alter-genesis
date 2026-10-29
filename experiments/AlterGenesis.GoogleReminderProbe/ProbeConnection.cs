using System.Reflection;
using AlterGenesis.NotificationProbe.Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Json;
using Google.Apis.Services;

namespace AlterGenesis.GoogleReminderProbe;

internal sealed class ProbeConnection(string directory) : IDisposable
{
    private const string UserKey = "calendar-user";
    internal static readonly string[] Scopes =
    [CalendarService.Scope.CalendarCalendarlistReadonly, CalendarService.Scope.CalendarEventsReadonly];
    private readonly ProtectedGoogleStore _store = new(Path.Combine(directory, "google"));
    private GoogleAuthorizationCodeFlow? _flow;
    private CalendarService? _service;

    internal async Task<CalendarService> OpenAsync(bool interactive, CancellationToken cancellationToken)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AlterGenesis.GoogleReminderProbe.Client")
            ?? throw new ProbeException("ClientNotPrepared");
        using var reader = new StreamReader(stream);
        var client = NewtonsoftJsonSerializer.Instance.Deserialize<ClientSecrets>(await reader.ReadToEndAsync(cancellationToken));
        if (client is null || string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.ClientSecret)
            || !client.ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            throw new ProbeException("ClientInvalid");
        var savedClient = await _store.GetAsync<ClientSecrets>("client");
        if (savedClient is not null && savedClient.ClientId != client.ClientId)
            throw new ProbeException("ClientDoesNotMatchIsolatedStore");
        if (savedClient is null) await _store.StoreAsync("client", client);

        var savedToken = await _store.GetAsync<TokenResponse>(UserKey);
        UserCredential? credential = null;
        if (savedToken is not null)
        {
            _flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            { ClientSecrets = client, Scopes = Scopes, DataStore = _store });
            credential = new UserCredential(_flow, UserKey, savedToken);
            try { await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken); }
            catch (TokenResponseException exception) when (exception.Error?.Error == "invalid_grant")
            {
                if (!interactive) throw new ProbeException("ReconnectRequired");
                // Only the isolated probe copy is removed, after an explicit --connect action.
                await _store.DeleteAsync<TokenResponse>(UserKey);
                credential = null;
                _flow.Dispose();
                _flow = null;
            }
        }
        if (credential is null)
        {
            if (!interactive) throw new ProbeException("ReconnectRequired");
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = client, Prompt = "select_account" },
                Scopes, UserKey, true, cancellationToken, _store);
            _flow = (GoogleAuthorizationCodeFlow)credential.Flow;
        }
        if (!string.IsNullOrWhiteSpace(credential.Token.Scope)
            && Scopes.Except(credential.Token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal).Any())
            throw new ProbeException("RequiredScopesNotGranted");
        _service = new CalendarService(new BaseClientService.Initializer
        { ApplicationName = "ALTER GENESIS", HttpClientInitializer = credential });
        _service.HttpClient.Timeout = TimeSpan.FromSeconds(30);
        return _service;
    }

    internal async Task<string[]> SelectedCalendarIdsAsync()
    {
        var saved = await _store.GetAsync<GoogleCalendarSnapshot>("catalog");
        if (saved is null || saved.Version != 1 || saved.Calendars is null || saved.SelectedIds is null)
            throw new ProbeException("CalendarSelectionRequired");
        if (saved.Calendars.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id))
            || saved.SelectedIds.Any(id => string.IsNullOrWhiteSpace(id) || !saved.Calendars.Any(item => item.Id == id)))
            throw new ProbeException("CalendarSelectionInvalid");
        var selected = saved.SelectedIds.Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) throw new ProbeException("CalendarSelectionRequired");
        return selected;
    }

    public void Dispose() { _service?.Dispose(); _flow?.Dispose(); }
}

internal sealed class ProbeException(string code) : Exception(code)
{
    internal string Code => Message;
}
