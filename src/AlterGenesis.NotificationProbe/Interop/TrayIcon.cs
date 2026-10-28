using Forms = System.Windows.Forms;

namespace AlterGenesis.NotificationProbe.Interop;

internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.NotifyIcon _icon;

    internal TrayIcon(string settingsText, string exitText, Action settings, Action exit)
    {
        _menu.Items.Add(settingsText, null, (_, _) => settings());
        _menu.Items.Add(exitText, null, (_, _) => exit());
        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "ALTER GENESIS",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) settings(); };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
