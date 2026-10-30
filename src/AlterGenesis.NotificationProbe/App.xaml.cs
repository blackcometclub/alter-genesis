using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using AlterGenesis.NotificationProbe.Diagnostics;
using AlterGenesis.NotificationProbe.Effects;
using AlterGenesis.NotificationProbe.Models;
using AlterGenesis.NotificationProbe.Settings;
using AlterGenesis.NotificationProbe.Startup;
using AlterGenesis.NotificationProbe.Notifications;
using AlterGenesis.NotificationProbe.Interop;
using AlterGenesis.NotificationProbe.Calendars;
using AlterGenesis.NotificationProbe.Google;

namespace AlterGenesis.NotificationProbe;

public partial class App : Application
{
    private WaveSoundPlayer? _player;
    private RepeatingSoundController? _soundRepeater;
    private AppSettingsStore _store = null!;
    private AppSettings _settings = AppSettings.Default;
    private IcalCalendarStore _calendarStore = null!;
    private readonly IcalFeedClient _icalClient = new();
    private SettingsWindow? _settingsWindow;
    private MainWindow? _notificationWindow;
    private Mutex? _instanceMutex;
    private string[] _args = [];
    private NotificationSchedule? _schedule;
    private DispatcherTimer? _reminderTimer;
    private TrayIcon? _tray;
    private readonly CancellationTokenSource _calendarSyncCancellation = new();
    private bool _calendarSyncing;
    private bool _exiting;
    private bool _dueFailureShown;
    private string? _runtimeReport;
    private bool _useGoogle;
    private GoogleNotificationSession? _googleSession;
    private GooglePeriodicRefresh? _googlePeriodicRefresh;
    private ResourceDictionary? _languageResources;
    private AppLanguage? _commandLanguage;
    private IStartupRegistration _startupRegistration = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ConfigureInitialLanguage(e.Args);
        _args = e.Args;
        _useGoogle = !e.Args.Contains("--ical", StringComparer.OrdinalIgnoreCase);

        if (ReadArgumentValue(e.Args, "--google-report") is { } googleReport)
        {
            Shutdown(await GoogleNotificationVerifier.RunAsync(googleReport) ? 0 : 1);
            return;
        }

        if (ReadArgumentValue(e.Args, "--snooze-report") is { } snoozeReport)
        {
            Shutdown(SnoozeVerifier.Run(snoozeReport) ? 0 : 1);
            return;
        }

        if (ReadArgumentValue(e.Args, "--ical-report") is { } icalReport)
        {
            Shutdown(await IcalVerifier.RunAsync(icalReport) ? 0 : 1);
            return;
        }

        if (ReadArgumentValue(e.Args, "--calendar-add-layout-report") is { } calendarAddLayoutReport)
        {
            var details = string.Equals(ReadArgumentValue(e.Args, "--calendar-add-step"), "details",
                StringComparison.OrdinalIgnoreCase);
            var window = new AddIcalCalendarWindow(_icalClient);
            if (details) window.ShowDetailsForLayout();
            window.ContentRendered += (_, _) => Shutdown(window.WriteLayoutReport(calendarAddLayoutReport, details) ? 0 : 1);
            MainWindow = window;
            window.Show();
            return;
        }

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

        if (ReadArgumentValue(e.Args, "--startup-report") is { } startupReport)
        {
            Shutdown(StartupRegistrationVerifier.Run(startupReport) ? 0 : 1);
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

        var explicitDataDirectory = ReadArgumentValue(e.Args, "--data-dir");
        var directory = explicitDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TINMOON", "AlterGenesis.NotificationProbe");
        foreach (var option in new[] { "--settings-layout-report", "--runtime-report", "--google-state-report", "--google-settings-report" })
            if (ReadArgumentValue(e.Args, option) is { } output && !DiagnosticOutputGuard.Allows(directory, output))
            {
                Shutdown(2);
                return; // A diagnostic must never overwrite settings, notifications or credentials.
            }
        _store = new AppSettingsStore(directory);
        _calendarStore = new IcalCalendarStore(directory);
        _startupRegistration = StartupRegistration.ForCurrentProcess(
            explicitDataDirectory is null ? null : Path.GetFullPath(directory));
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
            if (!e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
                MessageBox.Show((string)FindResource("Text.AlreadyRunning"), "ALTER GENESIS");
            Shutdown();
            return;
        }

        _settings = _store.Load();
        if (_commandLanguage is null) ApplyLanguage(_settings.Language);
        _player = new WaveSoundPlayer();
        _soundRepeater = new RepeatingSoundController(_player, () => _settings);
        if (ReadArgumentValue(e.Args, "--settings-layout-report") is not null)
        {
            ShowSettings();
            return;
        }

        _runtimeReport = ReadArgumentValue(e.Args, "--runtime-report");
        if (_runtimeReport is not null && ReadArgumentValue(e.Args, "--data-dir") is null)
        {
            Shutdown(1);
            return;
        }
        var notificationStore = new NotificationStore(_useGoogle ? Path.Combine(directory, "google-notifications") : directory);
        var saved = notificationStore.Load();
        if (notificationStore.LoadFailed)
        {
            ShowNotificationError("Text.NotificationsUnreadable");
            Shutdown(1);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _schedule = new NotificationSchedule(notificationStore, saved);
        _tray = new TrayIcon((string)FindResource("Text.Settings"), (string)FindResource("Text.Exit"),
            () => Dispatcher.Invoke(ShowSettings), () => Dispatcher.Invoke(() => RequestExit()));
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reminderTimer.Tick += (_, _) => ReminderTimerTick();

        if (_useGoogle)
        {
            _googleSession = new GoogleNotificationSession(new GoogleCalendarConnection(directory), _schedule,
                SynchronizeNotifications, () => (string)FindResource("Text.UntitledEvent"),
                allDay: () => (string)FindResource("Text.AllDay"));
            _googlePeriodicRefresh = new GooglePeriodicRefresh();
            _googleSession.Changed += GoogleSessionChanged;
            if (ReadArgumentValue(e.Args, "--google-state-report") is { } stateReport
                && ReadArgumentValue(e.Args, "--data-dir") is not null)
                _googleSession.Changed += () => WriteGoogleStateReport(stateReport);
            if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase)) ShowSettings();
            await _googleSession.RestoreAsync();
            if (_exiting) return;
            // Restored visible cards are shown without duplicating their original presentation sound.
            SynchronizeNotifications([]);
            if (!_googleSession.Connected || _googleSession.StatusKey is "Text.GoogleConnectionFailed"
                or "Text.GoogleConfigurationInvalid" or "Text.GoogleCredentialsUnreadable") ShowSettings();
            if (ReadArgumentValue(e.Args, "--google-settings-report") is { } settingsReport
                && ReadArgumentValue(e.Args, "--data-dir") is not null && _settingsWindow is { } settingsWindow)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                settingsWindow.WriteLayoutReport(settingsReport);
            }
            return;
        }

        var openSettings = e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase);
        var calendars = _calendarStore.Load();
        if (_calendarStore.LoadFailed && !openSettings)
            ShowNotificationError("Text.CalendarStoreUnreadable");
        _calendarSyncing = !openSettings && !_calendarStore.LoadFailed && calendars.Count > 0;
        if (!notificationStore.HasFile && !openSettings && !_calendarStore.LoadFailed
            && calendars.Count == 0
            && !_schedule.TryAdd(SampleNotifications.Create(_args)))
            ShowNotificationError("Text.NotificationsSaveFailed");
        if (!_schedule.TryReleaseDue(out _))
        {
            _dueFailureShown = true;
            ShowNotificationError("Text.ReminderSaveFailed");
        }
        if (openSettings || _schedule.Items.Count == 0 && !_calendarSyncing) ShowSettings();
        SynchronizeNotifications(_schedule.Visible);
        if (_calendarSyncing) await SynchronizeCalendarsOnceAsync(calendars);
    }

    private void WriteGoogleStateReport(string path)
    {
        if (_googleSession is null || _exiting) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            AtUtc = DateTimeOffset.UtcNow, _googleSession.Busy, _googleSession.Connected,
            _googleSession.StatusKey, _googleSession.FailureCode,
            CalendarCount = _googleSession.Snapshot.Calendars.Length,
            SelectedCount = _googleSession.Snapshot.SelectedIds.Length,
            ScheduledCount = _schedule?.Items.Count(item => item.Status == NotificationStatus.Scheduled),
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<bool> SynchronizeCalendarsOnceAsync(
        IReadOnlyCollection<IcalCalendarSubscription> calendars, bool showErrors = true)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var throughUtc = nowUtc.AddDays(30);
        var localTimeZone = TimeZoneInfo.Local;
        var readFailed = false;
        var saveFailed = false;
        try
        {
            var reads = await Task.WhenAll(calendars.Select(async calendar =>
            {
                try
                {
                    var snapshot = await _icalClient.ReadEventsAsync(calendar.SecretUrl,
                        nowUtc, throughUtc, localTimeZone, _calendarSyncCancellation.Token);
                    return new CalendarReadResult(calendar, snapshot, true);
                }
                catch (IcalFeedException)
                {
                    return new CalendarReadResult(calendar, null, false);
                }
                catch (OperationCanceledException) when (_calendarSyncCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    return new CalendarReadResult(calendar, null, false);
                }
            }));

            foreach (var read in reads)
            {
                if (!read.Succeeded || read.Snapshot is null)
                {
                    readFailed = true;
                    continue;
                }
                var scheduled = CalendarNotificationFactory.Create(read.Calendar, read.Snapshot,
                    nowUtc, throughUtc, localTimeZone,
                    (string)FindResource("Text.AllDay"), (string)FindResource("Text.UntitledEvent"));
                if (!_schedule!.TryReplaceCalendarSchedule(read.Calendar.Id, scheduled))
                {
                    saveFailed = true;
                }
            }
        }
        catch (OperationCanceledException) when (_calendarSyncCancellation.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            _calendarSyncing = false;
        }

        if (_exiting) return false;
        if (showErrors && readFailed) ShowNotificationError("Text.CalendarSyncFailed");
        if (showErrors && saveFailed) ShowNotificationError("Text.NotificationsSaveFailed");
        var released = _schedule!.TryReleaseDue(out var presented);
        if (showErrors && !released) ShowNotificationError("Text.ReminderSaveFailed");
        SynchronizeNotifications(presented);
        return !readFailed && !saveFailed && released;
    }

    private Task<bool> RefreshCalendarEventsAsync(
        IReadOnlyCollection<IcalCalendarSubscription> calendars)
    {
        if (_calendarSyncing || _exiting || _schedule is null || calendars.Count == 0)
            return Task.FromResult(false);
        _calendarSyncing = true;
        return SynchronizeCalendarsOnceAsync(calendars, showErrors: false);
    }

    private void AddTestNotifications()
    {
        var notifications = SampleNotifications.Create(_args);
        if (_schedule is null || !_schedule.TryAdd(notifications))
        {
            ShowNotificationError("Text.NotificationsSaveFailed");
            return;
        }
        SynchronizeNotifications(notifications);
    }

    private void NotificationAction(NotificationItem item, int? minutes)
    {
        if (_schedule is null) return;
        var saved = minutes is { } delay ? _schedule.TrySnooze(item.Id, delay) : _schedule.TryDismiss(item.Id);
        if (!saved)
        {
            ShowNotificationError("Text.NotificationsSaveFailed");
            return;
        }
        SynchronizeNotifications([]);
    }

    private void ReleaseDueNotifications()
    {
        if (_schedule is null || _exiting || _googleSession is { Busy: true } or { ScheduleBlocked: true }) return;
        if (!_schedule.TryReleaseDue(out var presented))
        {
            if (!_dueFailureShown)
            {
                _dueFailureShown = true;
                ShowNotificationError("Text.ReminderSaveFailed");
            }
            return;
        }
        _dueFailureShown = false;
        if (presented.Count > 0) SynchronizeNotifications(presented);
    }

    private void ReminderTimerTick()
    {
        ReleaseDueNotifications();
        if (_schedule is null || _soundRepeater is null || _exiting) return;
        _soundRepeater.Tick(_schedule.Visible.Count > 0);
        if (_googleSession is not null && _googlePeriodicRefresh?.TryTakeDue(
                _googleSession.Connected, _googleSession.Busy) == true)
            _ = RefreshGooglePeriodicallyAsync();
        UpdateReminderTimer();
    }

    private void GoogleSessionChanged()
    {
        if (_googleSession is null || _googlePeriodicRefresh is null || _exiting) return;
        _googlePeriodicRefresh.Update(_googleSession.Connected, _googleSession.Busy);
        UpdateReminderTimer();
    }

    private async Task RefreshGooglePeriodicallyAsync()
    {
        if (_googleSession is null || _googlePeriodicRefresh is null || _exiting) return;
        await _googleSession.RefreshAsync();
        if (_exiting) return;
        _googlePeriodicRefresh.Update(_googleSession.Connected, _googleSession.Busy);
        UpdateReminderTimer();
    }

    private void UpdateReminderTimer()
    {
        if (_reminderTimer is not null && _schedule is not null && _soundRepeater is not null)
            _reminderTimer.IsEnabled = _schedule.HasPending || _soundRepeater.IsActive
                || _googlePeriodicRefresh?.IsActive == true;
    }

    private void SynchronizeNotifications(IReadOnlyCollection<NotificationItem> presented)
    {
        if (_schedule is null || _exiting) return;
        var visible = _schedule.Visible;
        _soundRepeater!.Update(visible.Count > 0);
        UpdateReminderTimer();
        if (visible.Count == 0)
            _notificationWindow?.Close();
        else if (_notificationWindow is null)
        {
            var window = new MainWindow(visible, cardSize: _settings.CardSize);
            if (_args.Contains("--inspect", StringComparer.OrdinalIgnoreCase))
            {
                window.ShowInTaskbar = true;
                window.ShowActivated = true;
            }
            window.SettingsRequested += (_, _) => ShowSettings();
            window.NotificationActionRequested += NotificationAction;
            window.Closed += (_, _) =>
            {
                _notificationWindow = null;
                // Closing the whole panel exits; its unacknowledged cards stay saved.
                if (!_exiting && _schedule.Visible.Count > 0) RequestExit();
                else MaybeExit();
            };
            _notificationWindow = window;
            window.Show();
        }
        else
        {
            var wanted = visible.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var existing = _notificationWindow.Notifications.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in visible.Where(item => !existing.Contains(item.Id)))
                _notificationWindow.Notifications.Add(item);
            foreach (var obsolete in _notificationWindow.Notifications.Where(item => !wanted.Contains(item.Id)).ToArray())
                _notificationWindow.Notifications.Remove(obsolete);
        }
        PresentAfterRender(presented);
        MaybeExit();
    }

    private async void PresentAfterRender(IReadOnlyCollection<NotificationItem> presented)
    {
        if (presented.Count == 0) return;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (_exiting) return;
        await new SoundNotificationEffect(_player!, () => _settings.Sound).OnNotificationPresentedAsync(presented);
        _soundRepeater!.Update(_schedule!.Visible.Count > 0, resetCountdown: true);
        UpdateReminderTimer();
        if (_runtimeReport is { } path && _notificationWindow is { } window)
        {
            var layout = LayoutVerifier.Capture(window);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                Passed = layout.Passed, VisibleIds = _schedule!.Visible.Select(item => item.Id).ToArray(),
                PendingCount = _schedule.Items.Count(item => item.Status is NotificationStatus.Snoozed
                    or NotificationStatus.Scheduled),
                Layout = layout,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            LayoutVerifier.WritePreview(window, path);
            RequestExit();
        }
    }

    private void MaybeExit()
    {
        if (!_useGoogle && !_exiting && _schedule is not null && !_schedule.HasPending
            && !_calendarSyncing && _notificationWindow is null && _settingsWindow is null) RequestExit();
    }

    private void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        Shutdown();
    }

    private void ShowNotificationError(string key) => MessageBox.Show((string)FindResource(key),
        "ALTER GENESIS", MessageBoxButton.OK, MessageBoxImage.None);

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        var window = new SettingsWindow(_store, _player!, _settings, AddTestNotifications,
            _calendarStore, _icalClient, RefreshCalendarEventsAsync, _startupRegistration,
            _useGoogle, _googleSession);
        _settingsWindow = window;
        window.SettingsChanged += (_, _) =>
        {
            var sizeChanged = _settings.CardSize != window.SelectedSettings.CardSize;
            var languageChanged = _settings.Language != window.SelectedSettings.Language;
            var soundTimingChanged = _settings.Sound != window.SelectedSettings.Sound
                || _settings.SoundRepeat != window.SelectedSettings.SoundRepeat;
            _settings = window.SelectedSettings;
            if (languageChanged)
            {
                ApplyLanguage(_settings.Language);
                _tray?.UpdateText((string)FindResource("Text.Settings"), (string)FindResource("Text.Exit"));
                window.RefreshLanguage();
                _notificationWindow?.RefreshLanguage();
            }
            if (sizeChanged) _notificationWindow?.SetCardSize(_settings.CardSize);
            _soundRepeater?.Update(_schedule?.Visible.Count > 0, resetCountdown: soundTimingChanged);
            UpdateReminderTimer();
        };
        window.Closed += (_, _) => { _settingsWindow = null; MaybeExit(); };
        if (ReadArgumentValue(_args, "--settings-layout-report") is { } report)
            window.ContentRendered += (_, _) => Shutdown(window.WriteLayoutReport(report) ? 0 : 1);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _calendarSyncCancellation.Cancel();
        _reminderTimer?.Stop();
        _tray?.Dispose();
        _player?.Dispose();
        _icalClient.Dispose();
        if (_googleSession is not null) _googleSession.Changed -= GoogleSessionChanged;
        _googleSession?.Dispose();
        _calendarSyncCancellation.Dispose();
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private sealed record CalendarReadResult(IcalCalendarSubscription Calendar,
        IcalCalendarSnapshot? Snapshot, bool Succeeded);

    private void ConfigureInitialLanguage(IReadOnlyCollection<string> args)
    {
        var requestedLanguage = ReadArgumentValue(args, "--lang");
        _commandLanguage = requestedLanguage?.ToLowerInvariant() switch
        {
            "ja" or "ja-jp" => AppLanguage.Japanese,
            "en" or "en-us" => AppLanguage.English,
            _ => null,
        };
        ApplyLanguage(_commandLanguage ?? AppLanguage.System);
    }

    private void ApplyLanguage(AppLanguage language)
    {
        var resourceLanguage = AppLanguageResolver.Resolve(language, CultureInfo.CurrentUICulture);
        var next = new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{resourceLanguage}.xaml", UriKind.Relative),
        };
        if (_languageResources is not null) Resources.MergedDictionaries.Remove(_languageResources);
        Resources.MergedDictionaries.Insert(0, next);
        _languageResources = next;
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
