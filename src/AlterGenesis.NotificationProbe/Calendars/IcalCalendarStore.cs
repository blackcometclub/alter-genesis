using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlterGenesis.NotificationProbe.Calendars;

public sealed class IcalCalendarStore(string directory)
{
    private sealed record CalendarEnvelope(int Version, IReadOnlyList<IcalCalendarSubscription> Calendars);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TINMOON.AlterGenesis.IcalCalendars.v1");
    private static readonly Regex ColorPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string FilePath { get; } = Path.Combine(Path.GetFullPath(directory), "calendars.dpapi");
    public bool LoadFailed { get; private set; }

    public IReadOnlyList<IcalCalendarSubscription> Load()
    {
        byte[]? plain = null;
        try
        {
            var encrypted = File.ReadAllBytes(FilePath);
            plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var envelope = JsonSerializer.Deserialize<CalendarEnvelope>(plain, JsonOptions);
            if (envelope is not { Version: 1 } || envelope.Calendars.Any(calendar => !IsValid(calendar))
                || envelope.Calendars.Select(calendar => calendar.Id).Distinct(StringComparer.Ordinal).Count() != envelope.Calendars.Count)
                throw new InvalidDataException("Unsupported calendar settings.");
            return envelope.Calendars.ToArray();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CryptographicException or JsonException or InvalidDataException)
        {
            LoadFailed = true;
            return [];
        }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    public bool TrySave(IReadOnlyCollection<IcalCalendarSubscription> calendars)
    {
        if (LoadFailed || calendars.Any(calendar => !IsValid(calendar))
            || calendars.Select(calendar => calendar.Id).Distinct(StringComparer.Ordinal).Count() != calendars.Count)
            return false;

        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[]? plain = null;
        byte[]? encrypted = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            plain = JsonSerializer.SerializeToUtf8Bytes(new CalendarEnvelope(1, calendars.ToArray()), JsonOptions);
            encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath)) File.Replace(temporaryPath, FilePath, null);
            else File.Move(temporaryPath, FilePath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsValid(IcalCalendarSubscription calendar)
    {
        if (string.IsNullOrWhiteSpace(calendar.Id) || calendar.Id.Length > 64
            || string.IsNullOrWhiteSpace(calendar.Name) || calendar.Name.Length > 200
            || !ColorPattern.IsMatch(calendar.ColorHex)) return false;
        try { _ = IcalFeedClient.ValidateSecretUrl(calendar.SecretUrl); }
        catch (ArgumentException) { return false; }
        return true;
    }
}
