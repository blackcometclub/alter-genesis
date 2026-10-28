using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AlterGenesis.NotificationProbe.Effects;
using AlterGenesis.NotificationProbe.Google;
using AlterGenesis.NotificationProbe.Settings;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Http;
using Google.Apis.Services;

namespace AlterGenesis.NotificationProbe.Diagnostics;

internal static class GoogleConnectionVerifier
{
    internal static async Task<bool> RunAsync(string path)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var fixtures = Path.Combine(root, "google-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var results = new List<object>();
        var passed = true;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); results.Add(new { Name = name, Passed = true }); }
            catch (Exception exception)
            {
                passed = false;
                // Test data only. No production credentials are loaded by this verifier.
                results.Add(new { Name = name, Passed = false, Error = exception.ToString() });
            }
        }

        await Check("Only desktop OAuth client JSON is accepted", async () =>
        {
            var file = Path.Combine(fixtures, "fake-client.json");
            await File.WriteAllTextAsync(file, "{\"installed\":{\"client_id\":\"fake.apps.googleusercontent.com\",\"client_secret\":\"fake-secret\"}}");
            var client = await GoogleCalendarConnection.ReadDesktopClientAsync(file, default);
            Require(client.ClientId == "fake.apps.googleusercontent.com");
            await File.WriteAllTextAsync(file, "{\"web\":{\"client_id\":\"fake.apps.googleusercontent.com\",\"client_secret\":\"fake-secret\"}}");
            await MustFail<InvalidDataException>(() => GoogleCalendarConnection.ReadDesktopClientAsync(file, default));
        });
        await Check("Stored tokens are DPAPI ciphertext and round trip without plaintext files", async () =>
        {
            var directory = Path.Combine(fixtures, "encrypted");
            var store = new ProtectedGoogleStore(directory);
            var token = new TokenResponse { AccessToken = "isolated-fake-access", RefreshToken = "isolated-fake-refresh" };
            await store.StoreAsync("user", token);
            var restored = await store.GetAsync<TokenResponse>("user");
            Require(restored.AccessToken == token.AccessToken && restored.RefreshToken == token.RefreshToken);
            Require(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(store.PathFor<TokenResponse>("user")))
                .Contains(token.AccessToken, StringComparison.Ordinal));
            Require(Directory.GetFiles(directory).All(file => file.EndsWith(".dpapi", StringComparison.Ordinal)));
            Require(await store.GetAsync<TokenResponse>("missing") is null);
        });
        await Check("Ciphertext damage is reported without changing its bytes", async () =>
        {
            var store = new ProtectedGoogleStore(Path.Combine(fixtures, "damaged"));
            await store.StoreAsync("user", new TokenResponse { AccessToken = "fake" });
            var file = store.PathFor<TokenResponse>("user");
            var damaged = new byte[] { 1, 2, 3, 4 };
            await File.WriteAllBytesAsync(file, damaged);
            await MustFail<CryptographicException>(() => store.GetAsync<TokenResponse>("user"));
            Require((await File.ReadAllBytesAsync(file)).SequenceEqual(damaged));
        });
        await Check("Locked token replacement retains the previous token and removes the temporary file", async () =>
        {
            var directory = Path.Combine(fixtures, "locked");
            var store = new ProtectedGoogleStore(directory);
            await store.StoreAsync("user", new TokenResponse { AccessToken = "old-fake" });
            using (var locked = new FileStream(store.PathFor<TokenResponse>("user"), FileMode.Open, FileAccess.Read, FileShare.Read))
                await MustFail<IOException>(() => store.StoreAsync("user", new TokenResponse { AccessToken = "new-fake" }));
            Require((await store.GetAsync<TokenResponse>("user")).AccessToken == "old-fake");
            Require(Directory.GetFiles(directory).Length == 1);
        });
        await Check("Calendar selection is encrypted and survives reopening", async () =>
        {
            var directory = Path.Combine(fixtures, "selection");
            var snapshot = FakeConnection.Snapshot;
            await new ProtectedGoogleStore(directory).StoreAsync("catalog", snapshot);
            var restored = await new ProtectedGoogleStore(directory).GetAsync<GoogleCalendarSnapshot>("catalog");
            Require(restored.SelectedIds.SequenceEqual(snapshot.SelectedIds)
                && restored.Calendars.Select(item => item.Id).SequenceEqual(snapshot.Calendars.Select(item => item.Id)));
        });
        await Check("Calendar pagination retains IDs and colors, deduplicates and excludes free-busy-only access", async () =>
        {
            var handler = new CalendarHandler(false);
            using var service = Service(handler);
            var calendars = await GoogleCalendarCatalog.FetchAsync(service, default);
            Require(handler.Calls == 2 && calendars.Length == 2 && calendars[0].Id == "a"
                && calendars[0].Color == "#71859C" && calendars[1].Id == "b");
        });
        await Check("A later-page error fails the entire catalog fetch", async () =>
        {
            var handler = new CalendarHandler(true);
            using var service = Service(handler);
            await MustFail<global::Google.GoogleApiException>(() => GoogleCalendarCatalog.FetchAsync(service, default));
            Require(handler.Calls == 2);
        });
        await Check("Rendering a saved selection does not save again; toggling persists and failure restores choices", async () =>
        {
            var fake = new FakeConnection();
            var store = new AppSettingsStore(Path.Combine(fixtures, "ui"));
            var window = new SettingsWindow(store, new SilentPlayer(), AppSettings.Default, () => { }, fake);
            try
            {
                window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var checkBoxes = Children<CheckBox>(window).ToList();
                Require(checkBoxes.Count == 3 && fake.SaveCount == 0);
                var work = checkBoxes.Single(item => AutomationProperties.GetName(item) == "Work");
                work.IsChecked = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(fake.Saved.SequenceEqual(new[] { "a", "b" }));
                Require(window.WriteLayoutReport(Path.Combine(root, "google-settings-connected.json")));
                fake.FailSave = true;
                work.IsChecked = false;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(Children<CheckBox>(window).Single(item => AutomationProperties.GetName(item) == "Work").IsChecked == true);
                Require(window.WriteLayoutReport(Path.Combine(root, "google-settings-preview.json")));
                var heights = Children<Button>(window).Where(button => button.IsVisible).Select(button => button.ActualHeight).ToArray();
                Require(heights.Length > 0 && heights.All(height => Math.Abs(height - 32) <= 1));
                var panel = (GoogleConnectionPanel)window.FindName("GooglePanel");
                var refresh = (Button)panel.FindName("RefreshButton");
                var disconnect = (Button)panel.FindName("DisconnectButton");
                Require(Math.Abs(refresh.TranslatePoint(new Point(), window).Y
                    - disconnect.TranslatePoint(new Point(), window).Y) <= 1);
            }
            finally { window.Close(); }
        });
        await Check("A canceled connection leaves its panel disconnected", async () =>
        {
            var fake = new FakeConnection { CancelRestore = true };
            var window = new SettingsWindow(new AppSettingsStore(Path.Combine(fixtures, "canceled")),
                new SilentPlayer(), AppSettings.Default, () => { }, fake);
            try
            {
                window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var panel = (GoogleConnectionPanel)window.FindName("GooglePanel");
                Require(((Button)panel.FindName("ConnectButton")).IsVisible
                    && !((Button)panel.FindName("DisconnectButton")).IsVisible);
            }
            finally { window.Close(); }
        });
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Passed = passed, Fixtures = fixtures, Checks = results },
            new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static CalendarService Service(CalendarHandler handler) => new(new BaseClientService.Initializer
        { ApplicationName = "AlterGenesis-Isolated-Test", HttpClientFactory = new FakeHttpFactory(handler) });
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Check failed."); }
    private static async Task MustFail<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T item) yield return item;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
    private sealed class FakeHttpFactory(CalendarHandler handler) : IHttpClientFactory
    {
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) => new(new ConfigurableMessageHandler(handler));
    }
    private sealed class CalendarHandler(bool failSecondPage) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(request.Method == HttpMethod.Get && request.RequestUri!.Host == "www.googleapis.com");
            Calls++;
            if (Calls > 1 && failSecondPage)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("{\"error\":{\"code\":400,\"message\":\"isolated failure\"}}") });
            var body = Calls == 1
                ? "{\"items\":[{\"id\":\"a\",\"summary\":\"Personal\",\"backgroundColor\":\"#71859C\",\"primary\":true,\"accessRole\":\"owner\"},{\"id\":\"busy\",\"accessRole\":\"freeBusyReader\"}],\"nextPageToken\":\"page2\"}"
                : "{\"items\":[{\"id\":\"b\",\"summary\":\"Work\",\"backgroundColor\":\"#A86F55\",\"accessRole\":\"reader\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class SilentPlayer : INotificationSoundPlayer
    {
        public bool TryPlay(NotificationSound sound) => true;
        public void Stop() { }
    }
    private sealed class FakeConnection : IGoogleCalendarConnection
    {
        internal static GoogleCalendarSnapshot Snapshot => new(1,
            [new("a", "Personal", "#71859C", true), new("b", "Work", "#A86F55", false),
             new("c", "Long calendar name for checking truncation and its rounded tooltip", "#7F6A98", false)], ["a"]);
        internal int SaveCount;
        internal bool FailSave;
        internal bool CancelRestore;
        internal string[] Saved = ["a"];
        public bool HasClientConfiguration => true;
        public Task<GoogleCalendarSnapshot?> RestoreAsync(CancellationToken token) => CancelRestore
            ? Task.FromCanceled<GoogleCalendarSnapshot?>(new CancellationToken(true))
            : Task.FromResult<GoogleCalendarSnapshot?>(Snapshot);
        public Task<GoogleCalendarSnapshot> ConnectAsync(string? path, CancellationToken token) => Task.FromResult(Snapshot);
        public Task<GoogleCalendarSnapshot> RefreshAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task SaveSelectionAsync(IReadOnlyCollection<string> ids, CancellationToken token)
        {
            if (FailSave) throw new IOException("Isolated save failure");
            SaveCount++;
            Saved = ids.ToArray();
            return Task.CompletedTask;
        }
    }
}
