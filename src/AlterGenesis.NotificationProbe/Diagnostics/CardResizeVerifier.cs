using System.IO;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using AlterGenesis.NotificationProbe.Models;

namespace AlterGenesis.NotificationProbe.Diagnostics;

internal static class CardResizeVerifier
{
    internal static async Task<bool> RunAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var window = new MainWindow(SampleNotifications.Create([]), cardSize: NotificationCardSize.Large);
        var steps = new List<object>();
        var passed = true;
        try
        {
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var handle = new WindowInteropHelper(window).Handle;

            void Capture(string name, string[] expectedTitles)
            {
                var layout = LayoutVerifier.Capture(window);
                var sameWindow = new WindowInteropHelper(window).Handle == handle && handle != IntPtr.Zero;
                var sameNotifications = window.Notifications.Select(item => item.Title).SequenceEqual(expectedTitles);
                passed &= layout.Passed && sameWindow && sameNotifications;
                steps.Add(new { Name = name, SameWindow = sameWindow, SameNotifications = sameNotifications,
                    RequestedWidth = window.Width, FixedMinimumWidth = window.MinWidth,
                    FixedMaximumWidth = window.MaxWidth, Layout = layout });
                LayoutVerifier.WritePreview(window, Path.Combine(Path.GetDirectoryName(fullPath)!,
                    Path.GetFileNameWithoutExtension(fullPath) + "-" + name + ".json"));
            }

            var originalTitles = window.Notifications.Select(item => item.Title).ToArray();
            Capture("large", originalTitles);
            window.SetCardSize(NotificationCardSize.Small);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture("small", originalTitles);

            window.Notifications.RemoveAt(0);
            var remainingTitles = originalTitles.Skip(1).ToArray();
            window.SetCardSize(NotificationCardSize.Large);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture("large-after-dismiss", remainingTitles);
            window.SetCardSize(NotificationCardSize.Small);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture("small-after-dismiss", remainingTitles);

            File.WriteAllText(fullPath, JsonSerializer.Serialize(new { Passed = passed, Steps = steps },
                new JsonSerializerOptions { WriteIndented = true }));
            return passed;
        }
        catch (Exception exception)
        {
            File.WriteAllText(fullPath, JsonSerializer.Serialize(new { Passed = false, Error = exception.ToString() }));
            return false;
        }
        finally
        {
            window.Close();
        }
    }
}
