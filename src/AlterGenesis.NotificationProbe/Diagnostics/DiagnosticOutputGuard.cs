using System.IO;

namespace AlterGenesis.NotificationProbe.Diagnostics;

internal static class DiagnosticOutputGuard
{
    internal static bool Allows(string directory, string output)
    {
        var root = Path.GetFullPath(directory);
        var target = Path.GetFullPath(output);
        var reserved = new[] { "settings.json", "notifications.json", "calendars.dpapi",
            Path.Combine("google-notifications", "notifications.json") };
        return !reserved.Any(name => string.Equals(Path.Combine(root, name), target, StringComparison.OrdinalIgnoreCase))
            && !target.StartsWith(Path.Combine(root, "google") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
