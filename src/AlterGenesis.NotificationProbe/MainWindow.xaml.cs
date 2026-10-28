using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AlterGenesis.NotificationProbe.Diagnostics;
using AlterGenesis.NotificationProbe.Interop;
using AlterGenesis.NotificationProbe.Models;

namespace AlterGenesis.NotificationProbe;

public partial class MainWindow : Window
{
    private const double ScreenMargin = 24;
    private readonly string? _layoutReportPath;

    public MainWindow(
        IReadOnlyCollection<NotificationItem> notifications,
        string? layoutReportPath = null,
        NotificationCardSize cardSize = NotificationCardSize.Small)
    {
        Notifications = new ObservableCollection<NotificationItem>(notifications);
        _layoutReportPath = layoutReportPath;
        SetCardSize(cardSize);

        InitializeComponent();
        DataContext = this;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        SizeChanged += (_, _) => { if (IsLoaded) PositionAtWorkAreaBottomRight(); };
        Notifications.CollectionChanged += OnNotificationsChanged;
    }

    public ObservableCollection<NotificationItem> Notifications { get; }
    public NotificationCardSize CardSize { get; private set; }

    public void SetCardSize(NotificationCardSize size)
    {
        CardSize = size;
        NotificationCardLayout.For(size).Apply(Resources);
    }
    public event EventHandler? SettingsRequested;

    private void OpenSettingsClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        DwmWindow.Apply(new WindowInteropHelper(this).Handle);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionAtWorkAreaBottomRight();
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        PositionAtWorkAreaBottomRight();

        if (string.IsNullOrWhiteSpace(_layoutReportPath))
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            var report = LayoutVerifier.Capture(this);
            LayoutVerifier.Write(_layoutReportPath, report);
            LayoutVerifier.WritePreview(this, _layoutReportPath);
            Application.Current.Shutdown(report.Passed ? 0 : 1);
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OnNotificationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Notifications.Count == 0)
        {
            Close();
            return;
        }

        Dispatcher.BeginInvoke(PositionAtWorkAreaBottomRight, DispatcherPriority.Loaded);
    }

    private void CloseNotificationClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NotificationItem notification })
        {
            Notifications.Remove(notification);
        }
    }

    private void TitleTextLoaded(object sender, RoutedEventArgs e) =>
        UpdateTitleToolTip(sender);

    private void TitleTextSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateTitleToolTip(sender);

    private static void UpdateTitleToolTip(object sender)
    {
        if (sender is not TextBlock { Tag: Button button } text)
        {
            return;
        }

        // Measure the same font without a width constraint; short titles need no duplicate tooltip.
        var fullTitle = new TextBlock
        {
            Text = text.Text,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            FontWeight = text.FontWeight,
            FontStyle = text.FontStyle,
            FontStretch = text.FontStretch,
            FlowDirection = text.FlowDirection,
            Language = text.Language,
        };
        TextOptions.SetTextFormattingMode(fullTitle, TextOptions.GetTextFormattingMode(text));
        fullTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ToolTipService.SetIsEnabled(button, fullTitle.DesiredSize.Width > text.ActualWidth + 0.5);
    }

    private void SnoozeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox
            {
                IsLoaded: true,
                SelectedIndex: > 0,
                SelectedItem: ComboBoxItem { IsEnabled: true },
                Tag: NotificationItem notification,
            })
        {
            return;
        }

        // Interaction preview only; scheduling and persistence belong to the next work unit.
        Dispatcher.BeginInvoke(() => Notifications.Remove(notification));
    }

    private void OpenCalendarClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NotificationItem notification })
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(notification.EventUri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            System.Media.SystemSounds.Exclamation.Play();
        }
    }

    private void PositionAtWorkAreaBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - ActualWidth - ScreenMargin);
        Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight - ScreenMargin);
    }
}
