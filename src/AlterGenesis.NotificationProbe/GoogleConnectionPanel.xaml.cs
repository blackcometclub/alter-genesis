using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlterGenesis.NotificationProbe.Google;
using Microsoft.Win32;

namespace AlterGenesis.NotificationProbe;

public partial class GoogleConnectionPanel : UserControl
{
    private IGoogleCalendarConnection? _connection;
    private CancellationTokenSource? _operation;
    private GoogleCalendarSnapshot _snapshot = GoogleCalendarSnapshot.Empty;
    private bool _connected;
    private bool _rendering;
    private bool _loaded;
    private List<CalendarChoice> _choices = [];

    public GoogleConnectionPanel() => InitializeComponent();

    public void Initialize(IGoogleCalendarConnection? connection)
    {
        _connection = connection;
        ConnectButton.IsEnabled = connection is not null;
    }

    private async void PanelLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || _connection is null) return;
        _loaded = true;
        await RunAsync(async token =>
        {
            var restored = await _connection.RestoreAsync(token);
            if (restored is not null) ShowSnapshot(restored);
        }, false);
    }

    private void PanelUnloaded(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (_connection is null) return;
        string? path = null;
        if (!_connection.HasClientConfiguration)
        {
            var picker = new OpenFileDialog
            {
                Title = (string)FindResource("Text.GoogleChooseClient"),
                Filter = "JSON (*.json)|*.json", CheckFileExists = true,
            };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            path = picker.FileName;
        }
        await RunAsync(async token => ShowSnapshot(await _connection.ConnectAsync(path, token)), true);
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) =>
        await RunAsync(async token => ShowSnapshot(await _connection!.RefreshAsync(token)), false);

    private async void DisconnectClick(object sender, RoutedEventArgs e) =>
        await RunAsync(async token =>
        {
            await _connection!.DisconnectAsync(token);
            _connected = false;
            ShowSnapshot(GoogleCalendarSnapshot.Empty, false);
        }, false);

    private void CancelClick(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private async void CalendarSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_rendering || !_connected || _operation is not null) return;
        if (sender is CheckBox { DataContext: CalendarChoice choice } checkBox)
            choice.IsSelected = checkBox.IsChecked == true;
        var selected = _choices.Where(item => item.IsSelected).Select(item => item.Id).ToArray();
        if (selected.ToHashSet(StringComparer.Ordinal).SetEquals(_snapshot.SelectedIds)) return;
        await RunAsync(async token =>
        {
            try
            {
                await _connection!.SaveSelectionAsync(selected, token);
                _snapshot = _snapshot with { SelectedIds = selected };
            }
            catch { ShowSnapshot(_snapshot); throw new GoogleSelectionSaveException(); }
        }, false);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool authorization)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource(authorization ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(35));
        _operation = operation;
        StatusText.Visibility = Visibility.Collapsed;
        SetBusy(true);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (IsLoaded) ShowStatus("Text.GoogleCanceled"); }
        catch (GooglePermissionException) { ShowStatus("Text.GooglePermissionsRequired"); }
        catch (GoogleSelectionSaveException) { ShowStatus("Text.GoogleSelectionSaveFailed"); }
        catch (CryptographicException) { ShowStatus("Text.GoogleCredentialsUnreadable"); }
        catch (InvalidDataException) { ShowStatus("Text.GoogleConfigurationInvalid"); }
        catch (System.Text.Json.JsonException) { ShowStatus("Text.GoogleConfigurationInvalid"); }
        catch (Newtonsoft.Json.JsonException) { ShowStatus("Text.GoogleCredentialsUnreadable"); }
        catch (Exception) { ShowStatus("Text.GoogleConnectionFailed"); }
        finally
        {
            _operation = null;
            if (IsLoaded) SetBusy(false);
        }
    }

    private void ShowSnapshot(GoogleCalendarSnapshot snapshot, bool connected = true)
    {
        if (!IsLoaded) return;
        _snapshot = snapshot;
        _connected = connected;
        var selected = snapshot.SelectedIds.ToHashSet(StringComparer.Ordinal);
        _rendering = true;
        _choices = snapshot.Calendars.Select(item => new CalendarChoice(item, selected.Contains(item.Id),
            (string)FindResource("Text.GoogleUnnamedCalendar"))).ToList();
        CalendarChoices.ItemsSource = _choices;
        CalendarScroller.Visibility = connected && _choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _rendering = false;
        if (connected && _choices.Count == 0) ShowStatus("Text.GoogleNoCalendars");
        SetBusy(_operation is not null);
    }

    private void SetBusy(bool busy)
    {
        ConnectButton.Visibility = _connected ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.Visibility = DisconnectButton.Visibility = _connected ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsEnabled = RefreshButton.IsEnabled = DisconnectButton.IsEnabled = !busy && _connection is not null;
        CalendarChoices.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus(string key)
    {
        if (!IsLoaded) return;
        StatusText.Text = (string)FindResource(key);
        StatusText.Visibility = Visibility.Visible;
    }

    private void CalendarNameLoaded(object sender, RoutedEventArgs e) => UpdateNameToolTip(sender);
    private void CalendarNameSizeChanged(object sender, SizeChangedEventArgs e) => UpdateNameToolTip(sender);
    private static void UpdateNameToolTip(object sender)
    {
        if (sender is not TextBlock text) return;
        var full = new TextBlock { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
            FontWeight = text.FontWeight, Language = text.Language };
        full.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ToolTipService.SetIsEnabled(text, full.DesiredSize.Width > text.ActualWidth + 0.5);
    }

    private sealed class CalendarChoice
    {
        internal CalendarChoice(GoogleCalendarDescriptor calendar, bool selected, string unnamed)
        {
            Id = calendar.Id;
            Name = string.IsNullOrWhiteSpace(calendar.Name) ? unnamed : calendar.Name;
            IsSelected = selected;
            try { Color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(calendar.Color)); }
            catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException)
            { Color = new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 133, 156)); }
        }
        public string Id { get; }
        public string Name { get; }
        public Brush Color { get; }
        public bool IsSelected { get; set; }
    }
}

internal sealed class GoogleSelectionSaveException : Exception;
