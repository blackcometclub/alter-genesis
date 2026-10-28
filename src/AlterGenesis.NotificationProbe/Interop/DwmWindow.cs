using System.Runtime.InteropServices;

namespace AlterGenesis.NotificationProbe.Interop;

internal static class DwmWindow
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    public static void Apply(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var enabled = 1;
        _ = DwmSetWindowAttribute(
            windowHandle,
            DwmwaUseImmersiveDarkMode,
            ref enabled,
            sizeof(int));

        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(
            windowHandle,
            DwmwaWindowCornerPreference,
            ref preference,
            sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
