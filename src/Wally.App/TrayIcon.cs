using System.Windows;
using Application = System.Windows.Application;
using WinFormsApp = System.Windows.Forms;

namespace Wally.App;

/// <summary>
/// Keeps wally running in the background via a system-tray icon.
///
/// Closing the window hides it to the tray rather than exiting; the app only
/// truly quits from the tray menu's "Quit". The tray icon is drawn at runtime
/// (GDI+) to match the brand mark — a white rounded tile with a dark monitor —
/// so there is no external .ico file to ship.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinFormsApp.NotifyIcon _notifyIcon;
    private readonly Window _window;
    private bool _reallyQuitting;
    private bool _disposed;

    public TrayIcon(Window window)
    {
        _window = window;

        _notifyIcon = new WinFormsApp.NotifyIcon
        {
            Icon = BrandMark.ToIcon(32),
            Visible = true,
            Text = AppInfo.NameWithVersion,
        };

        var menu = new WinFormsApp.ContextMenuStrip();
        menu.Items.Add("Open wally", null, (_, _) => ShowWindow());
        menu.Items.Add(new WinFormsApp.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        // Intercept the window close: hide to tray instead of exiting.
        _window.Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyQuitting)
        {
            return;
        }

        e.Cancel = true;
        _window.Hide();
        _notifyIcon.ShowBalloonTip(
            2000, "wally", "Still running — right-click the tray icon to quit.",
            WinFormsApp.ToolTipIcon.None);
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Quit()
    {
        _reallyQuitting = true;
        Dispose();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _window.Closing -= OnWindowClosing;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _disposed = true;
    }
}
