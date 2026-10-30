using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AlterGenesis.NotificationProbe.Effects;
using AlterGenesis.NotificationProbe.Interop;
using AlterGenesis.NotificationProbe.Models;
using AlterGenesis.NotificationProbe.Settings;
using AlterGenesis.NotificationProbe.Diagnostics;
using AlterGenesis.NotificationProbe.Calendars;
using AlterGenesis.NotificationProbe.Google;

namespace AlterGenesis.NotificationProbe;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsStore _store;
    private readonly INotificationSoundPlayer _player;
    private readonly Action _showNotification;
    private readonly IcalCalendarStore _calendarStore;
    private readonly IcalFeedClient _icalClient;
    private readonly Func<IReadOnlyCollection<IcalCalendarSubscription>, Task<bool>> _refreshCalendarEvents;
    private bool _ready;
    private bool _calendarLoadErrorShown;
    private bool _refreshingCalendarEvents;
    private readonly bool _useGoogle;

    internal SettingsWindow(AppSettingsStore store, INotificationSoundPlayer player,
        AppSettings selectedSettings, Action showNotification,
        IcalCalendarStore calendarStore, IcalFeedClient icalClient,
        Func<IReadOnlyCollection<IcalCalendarSubscription>, Task<bool>> refreshCalendarEvents,
        bool useGoogle = false, GoogleNotificationSession? googleSession = null)
    {
        _store = store;
        _player = player;
        _showNotification = showNotification;
        _calendarStore = calendarStore;
        _icalClient = icalClient;
        _refreshCalendarEvents = refreshCalendarEvents;
        SelectedSettings = selectedSettings;
        _useGoogle = useGoogle;
        CalendarSubscriptions = new ObservableCollection<IcalCalendarSubscription>(useGoogle ? [] : _calendarStore.Load());
        InitializeComponent();
        if (useGoogle)
        {
            IcalHeading.Visibility = CalendarList.Visibility = IcalButtons.Visibility = Visibility.Collapsed;
            GooglePanel.Visibility = Visibility.Visible;
            GooglePanel.Initialize(googleSession);
        }
        CalendarSubscriptions.CollectionChanged += (_, _) => UpdateCalendarRefreshAvailability();
        UpdateCalendarRefreshAvailability();
        RestoreSelection();
        SourceInitialized += (_, _) => DwmWindow.Apply(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            CardSizeChoice.Focus();
            if (!useGoogle && _calendarStore.LoadFailed && !_calendarLoadErrorShown)
            {
                _calendarLoadErrorShown = true;
                ShowError("Text.CalendarStoreUnreadable");
            }
        };
        _ready = true;
    }

    public AppSettings SelectedSettings { get; private set; }
    public ObservableCollection<IcalCalendarSubscription> CalendarSubscriptions { get; }
    public event EventHandler? SettingsChanged;

    private void SoundSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SoundChoice.SelectedValue is not NotificationSound sound || sound == SelectedSettings.Sound)
            return;

        if (TryChangeSettings(SelectedSettings with { Sound = sound })) _player.Stop();
    }

    private void CardSizeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || CardSizeChoice.SelectedValue is not NotificationCardSize size || size == SelectedSettings.CardSize)
            return;
        TryChangeSettings(SelectedSettings with { CardSize = size });
    }

    private bool TryChangeSettings(AppSettings settings)
    {
        if (!_store.TrySave(settings))
        {
            RestoreSelection();
            ShowError(_store.LoadFailed ? "Text.SettingsUnreadable" : "Text.SettingsSaveFailed");
            return false;
        }

        SelectedSettings = settings;
        RestoreSelection();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void RestoreSelection()
    {
        _ready = false;
        SoundChoice.SelectedValue = SelectedSettings.Sound;
        CardSizeChoice.SelectedValue = SelectedSettings.CardSize;
        PreviewButton.IsEnabled = SelectedSettings.Sound != NotificationSound.None;
        _ready = true;
    }

    private void PreviewSoundClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_player.TryPlay(SelectedSettings.Sound)) ShowError("Text.SoundFailed");
        }
        catch (Exception)
        {
            ShowError("Text.SoundFailed");
        }
    }

    private void TestNotificationClick(object sender, RoutedEventArgs e) => _showNotification();

    private void AddCalendarClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AddIcalCalendarWindow(_icalClient) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } calendar) return;
        if (CalendarSubscriptions.Any(existing => string.Equals(existing.Id, calendar.Id, StringComparison.Ordinal)))
        {
            ShowError("Text.CalendarAlreadyAdded");
            return;
        }
        var next = CalendarSubscriptions.Append(calendar).ToArray();
        if (!_calendarStore.TrySave(next))
        {
            ShowError(_calendarStore.LoadFailed ? "Text.CalendarStoreUnreadable" : "Text.CalendarSaveFailed");
            return;
        }
        CalendarSubscriptions.Add(calendar);
    }

    private async void RefreshCalendarEventsClick(object sender, RoutedEventArgs e)
    {
        if (_refreshingCalendarEvents || CalendarSubscriptions.Count == 0) return;
        _refreshingCalendarEvents = true;
        CalendarRefreshStatus.Text = (string)FindResource("Text.CalendarEventsRefreshing");
        UpdateCalendarRefreshAvailability();
        try
        {
            var succeeded = await _refreshCalendarEvents(CalendarSubscriptions.ToArray());
            CalendarRefreshStatus.Text = (string)FindResource(succeeded
                ? "Text.CalendarEventsRefreshSucceeded"
                : "Text.CalendarEventsRefreshFailed");
        }
        catch (Exception)
        {
            CalendarRefreshStatus.Text = (string)FindResource("Text.CalendarEventsRefreshFailed");
        }
        finally
        {
            _refreshingCalendarEvents = false;
            UpdateCalendarRefreshAvailability();
            RefreshCalendarEventsButton.Focus();
        }
    }

    private void UpdateCalendarRefreshAvailability()
    {
        RefreshCalendarEventsButton.IsEnabled = !_refreshingCalendarEvents
            && !_calendarStore.LoadFailed && CalendarSubscriptions.Count > 0;
    }

    private async void RefreshCalendarClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not IcalCalendarSubscription calendar) return;
        button.IsEnabled = false;
        try
        {
            _ = await _icalClient.ReadAsync(calendar.SecretUrl);
            MessageBox.Show(this, (string)FindResource("Text.CalendarRefreshSucceeded"),
                (string)FindResource("Text.SettingsTitle"), MessageBoxButton.OK, MessageBoxImage.None);
        }
        catch (IcalFeedException exception)
        {
            ShowError(exception.Error switch
            {
                IcalFeedError.AccessDenied => "Text.CalendarAccessDenied",
                IcalFeedError.TooLarge => "Text.CalendarTooLarge",
                IcalFeedError.InvalidCalendar => "Text.CalendarInvalid",
                _ => "Text.CalendarConnectionFailed",
            });
        }
        finally { button.IsEnabled = true; }
    }

    private void RemoveCalendarClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not IcalCalendarSubscription calendar) return;
        var prompt = string.Format((string)FindResource("Text.CalendarRemoveConfirm"), calendar.Name);
        if (MessageBox.Show(this, prompt, (string)FindResource("Text.SettingsTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.None) != MessageBoxResult.Yes) return;
        var next = CalendarSubscriptions.Where(existing => !string.Equals(existing.Id, calendar.Id, StringComparison.Ordinal)).ToArray();
        if (!_calendarStore.TrySave(next))
        {
            ShowError(_calendarStore.LoadFailed ? "Text.CalendarStoreUnreadable" : "Text.CalendarSaveFailed");
            return;
        }
        CalendarSubscriptions.Remove(calendar);
    }

    private void ShowError(string key) => MessageBox.Show(this, (string)FindResource(key),
        (string)FindResource("Text.SettingsTitle"), MessageBoxButton.OK, MessageBoxImage.None);

    internal bool WriteLayoutReport(string path)
    {
        if (_useGoogle) return WriteGoogleLayoutReport(path);
        var choice = SoundChoice.TranslatePoint(new Point(), this);
        var preview = PreviewButton.TranslatePoint(new Point(), this);
        var test = TestNotificationButton.TranslatePoint(new Point(), this);
        var add = AddCalendarButton.TranslatePoint(new Point(), this);
        var refreshEvents = RefreshCalendarEventsButton.TranslatePoint(new Point(), this);
        var sizeChoice = CardSizeChoice.TranslatePoint(new Point(), this);
        var expectedHeight = (double)FindResource("Dimension.ControlHeight");
        var topDifference = Math.Abs(choice.Y - preview.Y);
        var bottomDifference = Math.Abs(choice.Y + SoundChoice.ActualHeight - preview.Y - PreviewButton.ActualHeight);
        var rightDifference = Math.Abs(preview.X + PreviewButton.ActualWidth - test.X - TestNotificationButton.ActualWidth);
        var calendarRightDifference = Math.Abs(add.X + AddCalendarButton.ActualWidth - test.X - TestNotificationButton.ActualWidth);
        var calendarButtonTopDifference = Math.Abs(add.Y - refreshEvents.Y);
        var calendarButtonBottomDifference = Math.Abs(add.Y + AddCalendarButton.ActualHeight
            - refreshEvents.Y - RefreshCalendarEventsButton.ActualHeight);
        var choiceLeftDifference = Math.Abs(sizeChoice.X - choice.X);
        var choiceWidthDifference = Math.Abs(CardSizeChoice.ActualWidth - SoundChoice.ActualWidth);
        var rowButtons = FindVisualChildren<Button>(CalendarList)
            .Where(button => button.CommandParameter is IcalCalendarSubscription).Take(2).ToArray();
        var calendarRowTopDifference = 0d;
        var calendarRowBottomDifference = 0d;
        var calendarRowHeightDifference = 0d;
        if (rowButtons.Length == 2)
        {
            var first = rowButtons[0].TranslatePoint(new Point(), this);
            var second = rowButtons[1].TranslatePoint(new Point(), this);
            calendarRowTopDifference = Math.Abs(first.Y - second.Y);
            calendarRowBottomDifference = Math.Abs(first.Y + rowButtons[0].ActualHeight - second.Y - rowButtons[1].ActualHeight);
            calendarRowHeightDifference = Math.Max(Math.Abs(rowButtons[0].ActualHeight - expectedHeight),
                Math.Abs(rowButtons[1].ActualHeight - expectedHeight));
        }
        var passed = topDifference <= 1 && bottomDifference <= 1 && rightDifference <= 1
            && Math.Abs(SoundChoice.ActualHeight - expectedHeight) <= 1
            && Math.Abs(PreviewButton.ActualHeight - expectedHeight) <= 1
            && Math.Abs(TestNotificationButton.ActualHeight - expectedHeight) <= 1
            && Math.Abs(AddCalendarButton.ActualHeight - expectedHeight) <= 1
            && Math.Abs(RefreshCalendarEventsButton.ActualHeight - expectedHeight) <= 1
            && Math.Abs(CardSizeChoice.ActualHeight - expectedHeight) <= 1
            && choiceLeftDifference <= 1 && choiceWidthDifference <= 1 && calendarRightDifference <= 1
            && calendarButtonTopDifference <= 1 && calendarButtonBottomDifference <= 1
            && calendarRowTopDifference <= 1 && calendarRowBottomDifference <= 1 && calendarRowHeightDifference <= 1;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Passed = passed, SelectedSound = SelectedSettings.Sound.ToString(),
            CardSize = SelectedSettings.CardSize.ToString(), ChoiceLeftDifference = choiceLeftDifference,
            ChoiceWidthDifference = choiceWidthDifference,
            ControlHeight = expectedHeight, TopDifference = topDifference,
            BottomDifference = bottomDifference, RightDifference = rightDifference,
            CalendarRightDifference = calendarRightDifference,
            CalendarButtonTopDifference = calendarButtonTopDifference,
            CalendarButtonBottomDifference = calendarButtonBottomDifference,
            CalendarCount = CalendarSubscriptions.Count,
            CalendarRowTopDifference = calendarRowTopDifference,
            CalendarRowBottomDifference = calendarRowBottomDifference,
            CalendarRowHeightDifference = calendarRowHeightDifference,
            Width = ActualWidth, Height = ActualHeight,
        }, new JsonSerializerOptions { WriteIndented = true }));
        LayoutVerifier.WritePreview(this, path);
        return passed;
    }

    private bool WriteGoogleLayoutReport(string path)
    {
        var controls = new FrameworkElement[] { CardSizeChoice, SoundChoice, PreviewButton, TestNotificationButton }
            .Concat(FindVisualChildren<Button>(GooglePanel).Where(button => button.IsVisible)).ToArray();
        var expected = (double)FindResource("Dimension.ControlHeight");
        var heightError = controls.Max(control => Math.Abs(control.ActualHeight - expected));
        var sound = SoundChoice.TranslatePoint(new Point(), this);
        var preview = PreviewButton.TranslatePoint(new Point(), this);
        var google = GooglePanel.TranslatePoint(new Point(), this);
        var size = CardSizeChoice.TranslatePoint(new Point(), this);
        var row = FindVisualChildren<Button>(GooglePanel).Where(button => button.IsVisible).ToArray();
        var rowYs = row.Select(control => control.TranslatePoint(new Point(), this).Y).ToArray();
        var rowError = rowYs.Length == 0 ? 0 : rowYs.Max() - rowYs.Min();
        var passed = heightError <= 1 && Math.Abs(sound.Y - preview.Y) <= 1
            && Math.Abs(sound.X - size.X) <= 1 && Math.Abs(sound.X - google.X) <= 1
            && Math.Abs(CardSizeChoice.ActualWidth - SoundChoice.ActualWidth) <= 1 && rowError <= 1;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Passed = passed, ControlHeight = expected,
            HeightError = heightError, RowTopError = rowError, SoundRowTopError = Math.Abs(sound.Y - preview.Y),
            Width = ActualWidth, Height = ActualHeight }, new JsonSerializerOptions { WriteIndented = true }));
        LayoutVerifier.WritePreview(this, path);
        return passed;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}
