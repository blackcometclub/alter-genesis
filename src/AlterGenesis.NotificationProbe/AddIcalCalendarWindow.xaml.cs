using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.IO;
using System.Text.Json;
using AlterGenesis.NotificationProbe.Calendars;
using AlterGenesis.NotificationProbe.Interop;

namespace AlterGenesis.NotificationProbe;

public partial class AddIcalCalendarWindow : Window
{
    public sealed record ColorOption(string Hex, string Name, Brush Brush);

    private static readonly (string Hex, string NameKey)[] Palette =
    [
        ("#71859C", "Text.CalendarColorBlue"),
        ("#A86F55", "Text.CalendarColorOrange"),
        ("#7F6A98", "Text.CalendarColorPurple"),
        ("#4E887C", "Text.CalendarColorGreen"),
        ("#927648", "Text.CalendarColorGold"),
        ("#8D6174", "Text.CalendarColorRose"),
    ];

    private readonly IcalFeedClient _client;
    private readonly CancellationTokenSource _closing = new();
    private string? _validatedUrl;

    public AddIcalCalendarWindow(IcalFeedClient client)
    {
        _client = client;
        InitializeComponent();
        ColorOptions = Palette.Select(item =>
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Hex));
            brush.Freeze();
            return new ColorOption(item.Hex, (string)FindResource(item.NameKey), brush);
        }).ToArray();
        ColorChoice.ItemsSource = ColorOptions;
        ColorChoice.SelectedIndex = 0;
        SourceInitialized += (_, _) => DwmWindow.Apply(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => SecretUrlBox.Focus();
        Closed += (_, _) => _closing.Cancel();
    }

    public IReadOnlyList<ColorOption> ColorOptions { get; }
    public IcalCalendarSubscription? Result { get; private set; }

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        var secretUrl = SecretUrlBox.Password;
        try { _ = IcalFeedClient.ValidateSecretUrl(secretUrl); }
        catch (ArgumentException)
        {
            ShowUrlError("Text.CalendarUrlInvalid");
            return;
        }

        UrlErrorText.Visibility = Visibility.Collapsed;
        ConnectButton.IsEnabled = false;
        ConnectButton.Content = FindResource("Text.Connecting");
        try
        {
            var preview = await _client.ReadAsync(secretUrl, _closing.Token);
            if (_closing.IsCancellationRequested) return;
            _validatedUrl = secretUrl;
            CalendarNameBox.Text = preview.Name;
            UrlStep.Visibility = Visibility.Collapsed;
            DetailsStep.Visibility = Visibility.Visible;
            CalendarNameBox.Focus();
            CalendarNameBox.SelectAll();
        }
        catch (IcalFeedException exception)
        {
            ShowUrlError(exception.Error switch
            {
                IcalFeedError.InvalidUrl => "Text.CalendarUrlInvalid",
                IcalFeedError.AccessDenied => "Text.CalendarAccessDenied",
                IcalFeedError.TooLarge => "Text.CalendarTooLarge",
                IcalFeedError.InvalidCalendar => "Text.CalendarInvalid",
                _ => "Text.CalendarConnectionFailed",
            });
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = FindResource("Text.Connect");
        }
    }

    private void AddClick(object sender, RoutedEventArgs e)
    {
        if (_validatedUrl is null) return;
        var name = CalendarNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowDetailsError("Text.CalendarNameRequired");
            return;
        }
        if (ColorChoice.SelectedItem is not ColorOption color) return;
        Result = IcalCalendarSubscription.Create(name, color.Hex, _validatedUrl);
        DialogResult = true;
    }

    private void BackClick(object sender, RoutedEventArgs e)
    {
        _validatedUrl = null;
        UrlErrorText.Visibility = Visibility.Collapsed;
        DetailsErrorText.Visibility = Visibility.Collapsed;
        DetailsStep.Visibility = Visibility.Collapsed;
        UrlStep.Visibility = Visibility.Visible;
        SecretUrlBox.Focus();
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SecretUrlChanged(object sender, RoutedEventArgs e) =>
        UrlErrorText.Visibility = Visibility.Collapsed;

    private void CalendarNameChanged(object sender, RoutedEventArgs e) =>
        DetailsErrorText.Visibility = Visibility.Collapsed;

    private void ShowUrlError(string key)
    {
        UrlErrorText.Text = (string)FindResource(key);
        UrlErrorText.Visibility = Visibility.Visible;
        SecretUrlBox.Focus();
    }

    private void ShowDetailsError(string key)
    {
        DetailsErrorText.Text = (string)FindResource(key);
        DetailsErrorText.Visibility = Visibility.Visible;
        CalendarNameBox.Focus();
    }

    internal void ShowDetailsForLayout()
    {
        CalendarNameBox.Text = "Sample Calendar";
        UrlStep.Visibility = Visibility.Collapsed;
        DetailsStep.Visibility = Visibility.Visible;
    }

    internal bool WriteLayoutReport(string path, bool details)
    {
        var expectedHeight = (double)FindResource("Dimension.ControlHeight");
        Point primary;
        Point secondary;
        double primaryHeight;
        double secondaryHeight;
        double fieldHeight;
        if (details)
        {
            primary = BackButton.TranslatePoint(new Point(), this);
            secondary = SaveButton.TranslatePoint(new Point(), this);
            primaryHeight = BackButton.ActualHeight;
            secondaryHeight = SaveButton.ActualHeight;
            fieldHeight = CalendarNameBox.ActualHeight;
        }
        else
        {
            primary = CancelButton.TranslatePoint(new Point(), this);
            secondary = ConnectButton.TranslatePoint(new Point(), this);
            primaryHeight = CancelButton.ActualHeight;
            secondaryHeight = ConnectButton.ActualHeight;
            fieldHeight = SecretUrlBox.ActualHeight;
        }
        var topDifference = Math.Abs(primary.Y - secondary.Y);
        var bottomDifference = Math.Abs(primary.Y + primaryHeight - secondary.Y - secondaryHeight);
        var passed = topDifference <= 1 && bottomDifference <= 1
            && Math.Abs(primaryHeight - expectedHeight) <= 1
            && Math.Abs(secondaryHeight - expectedHeight) <= 1
            && Math.Abs(fieldHeight - expectedHeight) <= 1
            && (!details || Math.Abs(ColorChoice.ActualHeight - expectedHeight) <= 1);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Passed = passed,
            Step = details ? "details" : "url",
            ControlHeight = expectedHeight,
            FieldHeight = fieldHeight,
            ColorChoiceHeight = ColorChoice.ActualHeight,
            TopDifference = topDifference,
            BottomDifference = bottomDifference,
            Width = ActualWidth,
            Height = ActualHeight,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Diagnostics.LayoutVerifier.WritePreview(this, path);
        return passed;
    }
}
