using System.Globalization;
using System.Windows;
using AlterGenesis.NotificationProbe.Effects;
using AlterGenesis.NotificationProbe.Models;

namespace AlterGenesis.NotificationProbe;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ConfigureLanguage(e.Args);

        var notifications = SampleNotifications.Create(e.Args);
        var layoutReportPath = ReadArgumentValue(e.Args, "--layout-report");
        var window = new MainWindow(notifications, layoutReportPath);

        if (e.Args.Any(argument =>
            string.Equals(argument, "--inspect", StringComparison.OrdinalIgnoreCase)))
        {
            window.ShowInTaskbar = true;
            window.ShowActivated = true;
        }

        MainWindow = window;
        window.Show();

        INotificationEffect notificationEffect = new NoOpNotificationEffect();
        await notificationEffect.OnNotificationPresentedAsync(notifications);
    }

    private void ConfigureLanguage(IReadOnlyCollection<string> args)
    {
        var requestedLanguage = ReadArgumentValue(args, "--lang");
        var language = requestedLanguage?.ToLowerInvariant() switch
        {
            "ja" or "ja-jp" => "ja-JP",
            "en" or "en-us" => "en-US",
            _ when CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" => "ja-JP",
            _ => "en-US",
        };

        Resources.MergedDictionaries.Insert(0, new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{language}.xaml", UriKind.Relative),
        });
    }

    private static string? ReadArgumentValue(IEnumerable<string> args, string name)
    {
        var prefix = name + "=";
        var inlineValue = args.FirstOrDefault(argument =>
            argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (inlineValue is not null)
        {
            return inlineValue[prefix.Length..];
        }

        var argumentList = args.ToList();
        var index = argumentList.FindIndex(argument =>
            string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

        return index >= 0 && index + 1 < argumentList.Count
            ? argumentList[index + 1]
            : null;
    }
}
