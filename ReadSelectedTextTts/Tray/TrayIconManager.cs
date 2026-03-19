using Forms = System.Windows.Forms;

namespace ReadSelectedTextTts.Tray;

public sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _toggleWindowMenuItem;

    public TrayIconManager()
    {
        var menu = new Forms.ContextMenuStrip();

        var readSelectionMenuItem = new Forms.ToolStripMenuItem("Read Selection");
        readSelectionMenuItem.Click += (_, _) => ReadSelectionRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(readSelectionMenuItem);

        var readClipboardMenuItem = new Forms.ToolStripMenuItem("Read Clipboard");
        readClipboardMenuItem.Click += (_, _) => ReadClipboardRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(readClipboardMenuItem);

        _toggleWindowMenuItem = new Forms.ToolStripMenuItem("Show");
        _toggleWindowMenuItem.Click += (_, _) => ToggleWindowRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_toggleWindowMenuItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitMenuItem = new Forms.ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exitMenuItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Read Selected Text TTS",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => ToggleWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ReadSelectionRequested;

    public event EventHandler? ReadClipboardRequested;

    public event EventHandler? ToggleWindowRequested;

    public event EventHandler? ExitRequested;

    public void SetWindowVisible(bool isVisible)
    {
        _toggleWindowMenuItem.Text = isVisible ? "Hide" : "Show";
    }

    public void ShowNotification(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        GC.SuppressFinalize(this);
    }
}
