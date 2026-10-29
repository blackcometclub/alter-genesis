using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AlterGenesis.NotificationProbe.Calendars;

namespace AlterGenesis.NotificationProbe.Diagnostics;

internal static class IcalVerifier
{
    public static async Task<bool> RunAsync(string reportPath)
    {
        var checks = new List<object>();
        var passed = true;
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!,
            "ical-checks-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(root);

        void Check(string name, bool result)
        {
            checks.Add(new { Name = name, Passed = result });
            passed &= result;
        }

        var fakeUrl = "https://calendar.google.com/calendar/ical/example/private-fake/basic.ics";
        var subscription = IcalCalendarSubscription.Create("Sample Calendar", "#71859C", fakeUrl);
        Check("accessible identity hides secret", subscription.ToString() == "Sample Calendar"
            && !subscription.ToString().Contains("private-fake", StringComparison.Ordinal));
        var store = new IcalCalendarStore(Path.Combine(root, "roundtrip"));
        Check("encrypted save", store.TrySave([subscription]));
        var storedBytes = File.ReadAllBytes(store.FilePath);
        var storedText = Encoding.UTF8.GetString(storedBytes);
        Check("secret absent from ciphertext", !storedText.Contains("private-fake", StringComparison.Ordinal));
        Check("name absent from ciphertext", !storedText.Contains("Sample Calendar", StringComparison.Ordinal));
        var restored = store.Load();
        Check("encrypted roundtrip", restored.Count == 1 && restored[0] == subscription);

        var brokenStore = new IcalCalendarStore(Path.Combine(root, "broken"));
        Directory.CreateDirectory(Path.GetDirectoryName(brokenStore.FilePath)!);
        var brokenBytes = Encoding.UTF8.GetBytes("not encrypted calendar data");
        File.WriteAllBytes(brokenStore.FilePath, brokenBytes);
        Check("broken data protected", brokenStore.Load().Count == 0 && brokenStore.LoadFailed);
        Check("broken data refuses overwrite", !brokenStore.TrySave([subscription])
            && File.ReadAllBytes(brokenStore.FilePath).SequenceEqual(brokenBytes));

        const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//ALTER GENESIS Test//EN\r\n"
            + "X-WR-CALNAME:Sample Calendar\r\n"
            + "BEGIN:VEVENT\r\nUID:one\r\nDTSTART:20261029T120000Z\r\nSUMMARY:One\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:two\r\nDTSTART:20261030T120000Z\r\nSUMMARY:Two\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n";
        using (var client = new IcalFeedClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
               { Content = new StringContent(ics, Encoding.UTF8, "text/calendar") })))
        {
            var preview = await client.ReadAsync(fakeUrl);
            Check("calendar name parsed", preview.Name == "Sample Calendar");
            Check("events parsed", preview.EventCount == 2);
        }

        using (var client = new IcalFeedClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))))
        {
            Check("access denied classified", await HasError(client, fakeUrl, IcalFeedError.AccessDenied));
        }
        Check("non Google URL rejected", ThrowsArgument(() =>
            IcalFeedClient.ValidateSecretUrl("https://example.com/calendar.ics")));
        Check("plain HTTP rejected", ThrowsArgument(() =>
            IcalFeedClient.ValidateSecretUrl("http://calendar.google.com/calendar.ics")));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            Passed = passed,
            CheckCount = checks.Count,
            Checks = checks,
            FixtureDirectory = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, root),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static async Task<bool> HasError(IcalFeedClient client, string url, IcalFeedError expected)
    {
        try { _ = await client.ReadAsync(url); }
        catch (IcalFeedException exception) { return exception.Error == expected && !exception.Message.Contains(url, StringComparison.Ordinal); }
        return false;
    }

    private static bool ThrowsArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return true; }
        return false;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
