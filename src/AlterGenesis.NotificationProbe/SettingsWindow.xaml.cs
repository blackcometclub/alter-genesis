using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AlterGenesis.NotificationProbe.Effects;
using AlterGenesis.NotificationProbe.Interop;
using AlterGenesis.NotificationProbe.Models;
using AlterGenesis.NotificationProbe.Settings;
using AlterGenesis.NotificationProbe.Diagnostics;
using AlterGenesis.NotificationProbe.Google;

namespace AlterGenesis.NotificationProbe;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsStore _store;
    private readonly INotificationSoundPlayer _player;
    private readonly Action _showNotification;
    private bool _ready;

    public SettingsWindow(AppSettingsStore store, INotificationSoundPlayer player,
        AppSettings selectedSettings, Action showNotification, IGoogleCalendarConnection? google = null)
    {
        _store = store;
        _player = player;
        _showNotification = showNotification;
        SelectedSettings = selectedSettings;
        InitializeComponent();
        GooglePanel.Initialize(google);
        RestoreSelection();
        SourceInitialized += (_, _) => DwmWindow.Apply(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => CardSizeChoice.Focus();
        _ready = true;
    }

    public AppSettings SelectedSettings { get; private set; }
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

    private void ShowError(string key) => MessageBox.Show(this, (string)FindResource(key),
        (string)FindResource("Text.SettingsTitle"), MessageBoxButton.OK, MessageBoxImage.None);

    internal bool WriteLayoutReport(string path)
    {
        var choice = SoundChoice.TranslatePoint(new Point(), this);
        var preview = PreviewButton.TranslatePoint(new Point(), this);
        var test = TestNotificationButton.TranslatePoint(new Point(), this);
        var sizeChoice = CardSizeChoice.TranslatePoint(new Point(), this);
        var expectedHeight = (double)FindResource("Dimension.ControlHeight");
        var topDifference = Math.Abs(choice.Y - preview.Y);
        var bottomDifference = Math.Abs(choice.Y + SoundChoice.ActualHeight - preview.Y - PreviewButton.ActualHeight);
        var rightDifference = Math.Abs(preview.X + PreviewButton.ActualWidth - test.X - TestNotificationButton.ActualWidth);
        var choiceLeftDifference = Math.Abs(sizeChoice.X - choice.X);
        var choiceWidthDifference = Math.Abs(CardSizeChoice.ActualWidth - SoundChoice.ActualWidth);
        var passed = topDifference <= 1 && bottomDifference <= 1 && rightDifference <= 1
            && Math.Abs(SoundChoice.ActualHeight - expectedHeight) <= 1
            && Math.Abs(PreviewButton.ActualHeight - expectedHeight) <= 1
            && Math.Abs(TestNotificationButton.ActualHeight - expectedHeight) <= 1
            && Math.Abs(CardSizeChoice.ActualHeight - expectedHeight) <= 1
            && choiceLeftDifference <= 1 && choiceWidthDifference <= 1;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Passed = passed, SelectedSound = SelectedSettings.Sound.ToString(),
            CardSize = SelectedSettings.CardSize.ToString(), ChoiceLeftDifference = choiceLeftDifference,
            ChoiceWidthDifference = choiceWidthDifference,
            ControlHeight = expectedHeight, TopDifference = topDifference,
            BottomDifference = bottomDifference, RightDifference = rightDifference,
            Width = ActualWidth, Height = ActualHeight,
        }, new JsonSerializerOptions { WriteIndented = true }));
        LayoutVerifier.WritePreview(this, path);
        return passed;
    }
}
