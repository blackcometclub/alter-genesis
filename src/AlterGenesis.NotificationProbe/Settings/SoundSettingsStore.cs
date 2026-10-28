using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlterGenesis.NotificationProbe.Effects;

namespace AlterGenesis.NotificationProbe.Settings;

public sealed class SoundSettingsStore(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public string FilePath { get; } = Path.Combine(Path.GetFullPath(directory), "settings.json");
    public bool LoadFailed { get; private set; }

    public NotificationSound Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<SoundSettings>(File.ReadAllText(FilePath), JsonOptions);
            if (settings is not { Version: 1 } || !Enum.IsDefined(settings.Sound))
                throw new InvalidDataException("Unsupported sound settings.");
            return settings.Sound;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return NotificationSound.Koron;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Preserve unreadable, malformed and newer-version files; do not overwrite them.
            LoadFailed = true;
            return NotificationSound.None;
        }
    }

    public bool TrySave(NotificationSound sound)
    {
        if (LoadFailed || !Enum.IsDefined(sound)) return false;
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new SoundSettings(1, sound), JsonOptions);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath)) File.Replace(temporaryPath, FilePath, null);
            else File.Move(temporaryPath, FilePath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record SoundSettings(int Version, NotificationSound Sound);
}
