using Forms = System.Windows.Forms;

namespace AlterGenesis.NotificationProbe.Interop;

internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripItem _settingsItem;
    private readonly Forms.ToolStripItem _exitItem;

    internal TrayIcon(string settingsText, string exitText, Action settings, Action exit)
    {
        _settingsItem = _menu.Items.Add(settingsText, null, (_, _) => settings());
        _exitItem = _menu.Items.Add(exitText, null, (_, _) => exit());
        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "ALTER GENESIS",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) settings(); };
    }

    internal void UpdateText(string settingsText, string exitText)
    {
        _settingsItem.Text = settingsText;
        _exitItem.Text = exitText;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
