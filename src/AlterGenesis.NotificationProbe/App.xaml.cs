using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using AlterGenesis.NotificationProbe.Diagnostics;
using AlterGenesis.NotificationProbe.Effects;
using AlterGenesis.NotificationProbe.Models;
using AlterGenesis.NotificationProbe.Settings;

namespace AlterGenesis.NotificationProbe;

public partial class App : Application
{
    private WaveSoundPlayer? _player;
    private AppSettingsStore _store = null!;
    private AppSettings _settings = AppSettings.Default;
    private SettingsWindow? _settingsWindow;
    private MainWindow? _notificationWindow;
    private Mutex? _instanceMutex;
    private string[] _args = [];

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ConfigureLanguage(e.Args);
        _args = e.Args;

        if (ReadArgumentValue(e.Args, "--resize-report") is { } resizeReport)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await CardResizeVerifier.RunAsync(resizeReport) ? 0 : 1);
            return;
        }

        if (ReadArgumentValue(e.Args, "--sound-report") is { } soundReport)
        {
            Shutdown(SoundVerifier.Run(soundReport) ? 0 : 1);
            return;
        }

        var layoutReportPath = ReadArgumentValue(e.Args, "--layout-report");
        if (layoutReportPath is not null)
        {
            var cardSize = ReadArgumentValue(e.Args, "--card-size")?.ToLowerInvariant() == "large"
                ? NotificationCardSize.Large : NotificationCardSize.Small;
            MainWindow = new MainWindow(SampleNotifications.Create(e.Args), layoutReportPath, cardSize);
            MainWindow.Show();
            return; // Layout inspection is deliberately silent and does not read user settings.
        }

        var directory = ReadArgumentValue(e.Args, "--data-dir") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TINMOON", "AlterGenesis.NotificationProbe");
        _store = new AppSettingsStore(directory);
        var identity = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        _instanceMutex = new Mutex(false, @"Local\TINMOON.AlterGenesis.NotificationProbe." + key);
        bool acquired;
        try { acquired = _instanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            MessageBox.Show((string)FindResource("Text.AlreadyRunning"), "ALTER GENESIS");
            Shutdown();
            return;
        }

        _settings = _store.Load();
        _player = new WaveSoundPlayer();
        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase)
            || ReadArgumentValue(e.Args, "--settings-layout-report") is not null)
            ShowSettings();
        else ShowNotifications();
    }

    private void ShowNotifications()
    {
        var notifications = SampleNotifications.Create(_args);
        var window = new MainWindow(notifications, cardSize: _settings.CardSize);
        if (_args.Contains("--inspect", StringComparer.OrdinalIgnoreCase))
        {
            window.ShowInTaskbar = true;
            window.ShowActivated = true;
        }
        window.SettingsRequested += (_, _) => ShowSettings();
        window.Closed += (_, _) => { if (_notificationWindow == window) _notificationWindow = null; };
        EventHandler? presented = null;
        presented = async (_, _) =>
        {
            window.ContentRendered -= presented;
            await new SoundNotificationEffect(_player!, () => _settings.Sound).OnNotificationPresentedAsync(notifications);
        };
        window.ContentRendered += presented;
        var previous = _notificationWindow;
        _notificationWindow = window;
        window.Show();
        previous?.Close();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        var window = new SettingsWindow(_store, _player!, _settings, ShowNotifications);
        _settingsWindow = window;
        window.SettingsChanged += (_, _) =>
        {
            var sizeChanged = _settings.CardSize != window.SelectedSettings.CardSize;
            _settings = window.SelectedSettings;
            if (sizeChanged) _notificationWindow?.SetCardSize(_settings.CardSize);
        };
        window.Closed += (_, _) => _settingsWindow = null;
        if (ReadArgumentValue(_args, "--settings-layout-report") is { } report)
            window.ContentRendered += (_, _) => Shutdown(window.WriteLayoutReport(report) ? 0 : 1);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _player?.Dispose();
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
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
