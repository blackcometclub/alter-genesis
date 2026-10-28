using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AlterGenesis.NotificationProbe.Notifications;

internal interface INotificationStore
{
    bool TrySave(IReadOnlyList<SavedNotification> items);
}

internal sealed class NotificationStore(string directory) : INotificationStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    internal string FilePath { get; } = Path.Combine(Path.GetFullPath(directory), "notifications.json");
    internal bool HasFile { get; private set; }
    internal bool LoadFailed { get; private set; }

    internal IReadOnlyList<SavedNotification> Load()
    {
        try
        {
            var data = JsonSerializer.Deserialize<NotificationData>(File.ReadAllText(FilePath), Options);
            if (data is not { Version: 1, Items: not null } || !IsValid(data.Items))
                throw new InvalidDataException("Invalid notification data.");
            HasFile = true;
            return data.Items;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LoadFailed = true;
            return [];
        }
    }

    public bool TrySave(IReadOnlyList<SavedNotification> items)
    {
        if (LoadFailed || !IsValid(items)) return false;
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new NotificationData(1, items), Options);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
            HasFile = true;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsValid(IReadOnlyList<SavedNotification> items) =>
        items.Select(item => item?.Id).Distinct(StringComparer.Ordinal).Count() == items.Count
        && items.All(item => item is not null && !string.IsNullOrWhiteSpace(item.Id)
            && !string.IsNullOrWhiteSpace(item.StartTime) && !string.IsNullOrWhiteSpace(item.Title)
            && item.CalendarColor is not null && Regex.IsMatch(item.CalendarColor, "^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")
            && Uri.TryCreate(item.EventUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && (item.Status == NotificationStatus.Visible && item.DueAtUtc is null
                || item.Status == NotificationStatus.Snoozed && item.DueAtUtc is not null));

    private sealed record NotificationData(int Version, IReadOnlyList<SavedNotification> Items);
}
