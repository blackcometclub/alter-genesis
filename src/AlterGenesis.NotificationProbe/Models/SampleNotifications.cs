using System.Windows.Media;

namespace AlterGenesis.NotificationProbe.Models;

internal static class SampleNotifications
{
    public static IReadOnlyList<NotificationItem> Create(IReadOnlyCollection<string> args)
    {
        var all = new List<NotificationItem>
        {
            CreateItem("15:00", "歯医者の予約", "#71859C"),
            CreateItem("17:30", "資料の提出", "#A86F55"),
            CreateItem("19:00", "通知の見え方と長い予定名の折り返しを確かめる", "#7F6A98"),
            CreateItem("20:15", "図書館の返却期限", "#4E887C"),
            CreateItem("21:00", "明日の準備", "#927648"),
            CreateItem("22:30", "薬を飲む", "#8D6174"),
        };

        if (args.Any(argument => string.Equals(argument, "--single", StringComparison.OrdinalIgnoreCase)))
        {
            return all.Take(1).ToList();
        }

        if (args.Any(argument => string.Equals(argument, "--many", StringComparison.OrdinalIgnoreCase)))
        {
            return all;
        }

        return all.Take(3).ToList();
    }

    private static NotificationItem CreateItem(string startTime, string title, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();

        return new NotificationItem(
            startTime,
            title,
            brush,
            new Uri("https://calendar.google.com/calendar/u/0/r"));
    }
}
