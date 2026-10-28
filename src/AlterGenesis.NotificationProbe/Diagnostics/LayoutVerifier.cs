using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlterGenesis.NotificationProbe.Models;

namespace AlterGenesis.NotificationProbe.Diagnostics;

internal static class LayoutVerifier
{
    private const double Tolerance = 1.0;

    public static LayoutReport Capture(MainWindow window)
    {
        window.UpdateLayout();

        var elements = FindVisualChildren<FrameworkElement>(window).ToList();
        var bands = FindByAutomationId<Border>(elements, "CalendarBand");
        var items = FindByAutomationId<Grid>(elements, "NotificationItem");
        var snoozeControls = FindByAutomationId<ComboBox>(elements, "SnoozeNotification");
        var closeButtons = FindByAutomationId<Button>(elements, "CloseNotification");
        var timeTexts = FindByAutomationId<TextBlock>(elements, "EventStartTime");
        var titleTexts = FindByAutomationId<TextBlock>(elements, "EventTitle");

        var rows = items.Select(item => MeasureControlRow(item, window)).ToList();
        var expectedRowHeight = window.CardSize == NotificationCardSize.Small ? 128 : 152;
        var expectedWidth = window.CardSize == NotificationCardSize.Small ? 480 : 560;
        var expectedMaxHeight = window.CardSize == NotificationCardSize.Small ? 514 : 602;
        var checks = new Dictionary<string, bool>
        {
            ["bandWidth40"] = bands.Count > 0 && bands.All(band => Near(band.ActualWidth, 40)),
            ["controlHeight32"] = snoozeControls.Count > 0
                && snoozeControls.All(control => Near(control.ActualHeight, 32))
                && closeButtons.All(button => Near(button.ActualHeight, 32)),
            ["controlEdgesAligned"] = rows.Count > 0 && rows.All(row => row.MaximumEdgeDelta <= Tolerance),
            ["eventHeightMatchesSize"] = items.Count > 0 && items.All(item => Near(item.ActualHeight, expectedRowHeight)),
            ["windowWidthMatchesSize"] = Near(window.ActualWidth, expectedWidth),
            ["windowHeightMatchesSize"] = Near(window.ActualHeight,
                Math.Min(items.Count * (expectedRowHeight + 1) + 2, expectedMaxHeight)),
            ["timeFont28"] = timeTexts.Count > 0 && timeTexts.All(text => Near(text.FontSize, 28)),
            ["titleFont26"] = titleTexts.Count > 0 && titleTexts.All(text => Near(text.FontSize, 26)),
            ["itemCountsMatch"] = items.Count == bands.Count
                && items.Count == snoozeControls.Count
                && items.Count == closeButtons.Count,
        };

        return new LayoutReport(
            checks.Values.All(value => value),
            checks,
            bands.Select(band => band.ActualWidth).ToList(),
            snoozeControls.Select(control => control.ActualHeight).ToList(),
            closeButtons.Select(button => button.ActualHeight).ToList(),
            rows,
            new WindowMetrics(window.ActualWidth, window.ActualHeight));
    }

    public static void Write(string path, LayoutReport report)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
    }

    internal static void WritePreview(Window window, string reportPath)
    {
        var content = (FrameworkElement)window.Content;
        // Render the WPF client area only; native title bars are outside this visual.
        var width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        var height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        var image = new RenderTargetBitmap((int)Math.Ceiling(width),
            (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.ChangeExtension(reportPath, ".png"));
        encoder.Save(output);
    }

    private static ControlRowMetrics MeasureControlRow(Grid item, Window window)
    {
        var controls = FindVisualChildren<FrameworkElement>(item).ToList();
        var snooze = FindByAutomationId<ComboBox>(controls, "SnoozeNotification").Single();
        var close = FindByAutomationId<Button>(controls, "CloseNotification").Single();
        var snoozeTop = snooze.TranslatePoint(new Point(0, 0), window).Y;
        var closeTop = close.TranslatePoint(new Point(0, 0), window).Y;
        var topDelta = Math.Abs(snoozeTop - closeTop);
        var bottomDelta = Math.Abs(
            (snoozeTop + snooze.ActualHeight) - (closeTop + close.ActualHeight));
        var heightDelta = Math.Abs(snooze.ActualHeight - close.ActualHeight);

        return new ControlRowMetrics(topDelta, bottomDelta, heightDelta);
    }

    private static List<T> FindByAutomationId<T>(
        IEnumerable<FrameworkElement> elements,
        string automationId)
        where T : FrameworkElement
    {
        return elements
            .OfType<T>()
            .Where(element => AutomationProperties.GetAutomationId(element) == automationId)
            .ToList();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool Near(double actual, double expected) =>
        Math.Abs(actual - expected) <= Tolerance;
}

internal sealed record LayoutReport(
    bool Passed,
    IReadOnlyDictionary<string, bool> Checks,
    IReadOnlyList<double> BandWidths,
    IReadOnlyList<double> SnoozeControlHeights,
    IReadOnlyList<double> CloseButtonHeights,
    IReadOnlyList<ControlRowMetrics> ControlRows,
    WindowMetrics Window);

internal sealed record ControlRowMetrics(
    double TopDelta,
    double BottomDelta,
    double HeightDelta)
{
    public double MaximumEdgeDelta => Math.Max(TopDelta, Math.Max(BottomDelta, HeightDelta));
}

internal sealed record WindowMetrics(double Width, double Height);
