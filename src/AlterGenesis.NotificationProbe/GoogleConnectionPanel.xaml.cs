using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlterGenesis.NotificationProbe.Google;

namespace AlterGenesis.NotificationProbe;

public partial class GoogleConnectionPanel : UserControl
{
    private GoogleNotificationSession? _session;
    private bool _rendering;
    private List<CalendarChoice> _choices = [];
    public GoogleConnectionPanel() => InitializeComponent();

    internal void Initialize(GoogleNotificationSession? session)
    {
        _session = session;
        Render();
    }

    internal void RefreshLanguage() => Render();

    private void PanelLoaded(object sender, RoutedEventArgs e)
    {
        if (_session is not null) _session.Changed += Render;
        Render();
    }
    private void PanelUnloaded(object sender, RoutedEventArgs e)
    {
        if (_session is not null) _session.Changed -= Render;
    }
    private async void ConnectClick(object sender, RoutedEventArgs e) { if (_session is not null) await _session.ConnectAsync(); }
    private async void RefreshClick(object sender, RoutedEventArgs e) { if (_session is not null) await _session.RefreshAsync(); }
    private async void DisconnectClick(object sender, RoutedEventArgs e) { if (_session is not null) await _session.DisconnectAsync(); }
    private void CancelClick(object sender, RoutedEventArgs e) => _session?.Cancel();
    private async void CalendarSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_rendering || _session is not { Connected: true, Busy: false }) return;
        if (sender is CheckBox { DataContext: CalendarChoice choice } box) choice.IsSelected = box.IsChecked == true;
        await _session.SelectAsync(_choices.Where(item => item.IsSelected).Select(item => item.Id).ToArray());
    }
    private void Render()
    {
        var connected = _session?.Connected == true;
        var busy = _session?.Busy == true;
        var snapshot = _session?.Snapshot ?? GoogleCalendarSnapshot.Empty;
        _rendering = true;
        _choices = snapshot.Calendars.Select(item => new CalendarChoice(item, snapshot.SelectedIds.Contains(item.Id),
            (string)FindResource("Text.GoogleUnnamedCalendar"))).ToList();
        CalendarChoices.ItemsSource = _choices;
        CalendarScroller.Visibility = connected && _choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _rendering = false;
        ConnectButton.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.Visibility = DisconnectButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsEnabled = RefreshButton.IsEnabled = DisconnectButton.IsEnabled = !busy && _session is not null;
        CalendarChoices.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        var status = _session?.StatusKey;
        StatusText.Text = status is null ? "" : (string)FindResource(status);
        StatusText.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
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
            Id = calendar.Id; Name = string.IsNullOrWhiteSpace(calendar.Name) ? unnamed : calendar.Name;
            IsSelected = selected;
            try { Color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(calendar.Color)); }
            catch { Color = new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 133, 156)); }
        }
        public string Id { get; }
        public string Name { get; }
        public Brush Color { get; }
        public bool IsSelected { get; set; }
    }
}
