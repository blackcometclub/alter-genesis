using System.Globalization;

namespace AlterGenesis.NotificationProbe.Settings;

public enum AppLanguage
{
    System,
    English,
    Japanese,
}

internal static class AppLanguageResolver
{
    internal static string Resolve(AppLanguage language, CultureInfo systemUiCulture) => language switch
    {
        AppLanguage.Japanese => "ja-JP",
        AppLanguage.English => "en-US",
        _ when systemUiCulture.TwoLetterISOLanguageName == "ja" => "ja-JP",
        _ => "en-US",
    };
}
