using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Apis.Auth.OAuth2.Responses;

namespace AlterGenesis.GoogleReminderProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--self-test") return await ProbeTests.RunAsync(args[1]);
            if (args.Length != 4 || args[0] is not ("--read" or "--connect")
                || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                throw new ProbeException("Usage: --self-test REPORT | --read DATE DATA_DIRECTORY REPORT | --connect DATE DATA_DIRECTORY REPORT");
            var dataDirectory = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(dataDirectory);
            using var gate = OpenGate(dataDirectory);
            using var cancellation = new CancellationTokenSource(args[0] == "--connect" ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(45));
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
            using var connection = new ProbeConnection(dataDirectory);
            var selected = await connection.SelectedCalendarIdsAsync();
            var service = await connection.OpenAsync(args[0] == "--connect", cancellation.Token);
            // Explicit Japan-day window for this first experiment; never the machine's implicit zone.
            var dayStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9));
            var results = new List<ReminderCalculation>();
            foreach (var id in selected)
                results.AddRange(await ReminderReader.ReadAsync(service, id, dayStart, "テスト", cancellation.Token));
            await WriteReportAsync(args[3], new { Kind = "GoogleReminderReadOnly", NotificationsEnabled = false,
                ReadAtUtc = DateTimeOffset.UtcNow, Date = day, CalendarCount = selected.Length,
                MatchedTests = results.Count, Results = results });
            Console.WriteLine($"ReadComplete: calendars={selected.Length}, matchedTests={results.Count}; notifications=disabled");
            return 0;
        }
        catch (ProbeException exception) { Console.Error.WriteLine(exception.Code); return 2; }
        catch (TokenResponseException exception) when (exception.Error?.Error == "invalid_grant")
        { Console.Error.WriteLine("ReconnectRequired"); return 2; }
        catch (OperationCanceledException) { Console.Error.WriteLine("CancelledOrTimedOut"); return 3; }
        catch (CryptographicException) { Console.Error.WriteLine("EncryptedStoreUnreadable"); return 4; }
        catch (Google.GoogleApiException exception)
        { Console.Error.WriteLine($"GoogleReadFailed: HTTP {(int)exception.HttpStatusCode}"); return 5; }
        catch (Exception)
        {
            // SDK exceptions can contain request URLs, account IDs or credentials. Never print them.
            Console.Error.WriteLine("ProbeFailed"); return 6;
        }
    }

    private static FileStream OpenGate(string directory)
    {
        try { return new FileStream(Path.Combine(directory, "probe.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new ProbeException("ProbeAlreadyRunningOrStoreLocked"); }
    }

    internal static async Task WriteReportAsync(string path, object value)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, full, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
