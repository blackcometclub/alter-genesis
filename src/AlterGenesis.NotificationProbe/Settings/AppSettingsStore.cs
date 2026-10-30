using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlterGenesis.NotificationProbe.Effects;
using AlterGenesis.NotificationProbe.Models;

namespace AlterGenesis.NotificationProbe.Settings;

public sealed record AppSettings(int Version, NotificationSound Sound,
    NotificationCardSize CardSize = NotificationCardSize.Large,
    NotificationSoundRepeat SoundRepeat = NotificationSoundRepeat.None,
    AppLanguage Language = AppLanguage.System)
{
    // Missing values preserve the legacy large layout, one-shot sound and system language.
    public static AppSettings Default => new(1, NotificationSound.Koron,
        NotificationCardSize.Small, NotificationSoundRepeat.None, AppLanguage.System);
}

public sealed class AppSettingsStore(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public string FilePath { get; } = Path.Combine(Path.GetFullPath(directory), "settings.json");
    public bool LoadFailed { get; private set; }

    public AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
            if (settings is not { Version: 1 } || !Enum.IsDefined(settings.Sound)
                || !Enum.IsDefined(settings.CardSize) || !Enum.IsDefined(settings.SoundRepeat)
                || !Enum.IsDefined(settings.Language))
                throw new InvalidDataException("Unsupported sound settings.");
            return settings;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return AppSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Preserve unreadable, malformed and newer-version files; do not overwrite them.
            LoadFailed = true;
            return AppSettings.Default with { Sound = NotificationSound.None };
        }
    }

    public bool TrySave(AppSettings settings)
    {
        if (LoadFailed || settings.Version != 1 || !Enum.IsDefined(settings.Sound)
            || !Enum.IsDefined(settings.CardSize) || !Enum.IsDefined(settings.SoundRepeat)
            || !Enum.IsDefined(settings.Language))
            return false;
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
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

}
