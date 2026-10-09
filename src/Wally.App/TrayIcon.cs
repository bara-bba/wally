using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using Application = System.Windows.Application;
using WinFormsApp = System.Windows.Forms;
using Point = System.Drawing.Point;

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
            Icon = BuildIcon(),
            Visible = true,
            Text = "wally",
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

    /// <summary>Draw the brand mark as an icon: white rounded tile + dark monitor.</summary>
    private static Icon BuildIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // White rounded tile.
            using var tile = new GraphicsPath();
            const int r = 7;
            var rect = new Rectangle(1, 1, size - 2, size - 2);
            tile.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
            tile.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
            tile.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
            tile.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
            tile.CloseFigure();
            g.FillPath(Brushes.White, tile);

            // Dark monitor screen (frame drawn as a filled dark rect with white inset).
            var dark = Color.FromArgb(0x20, 0x20, 0x20);
            using var darkBrush = new SolidBrush(dark);
            using var darkPen = new Pen(dark, 2.4f);

            var screen = new Rectangle(7, 8, 18, 13);
            g.DrawRectangle(darkPen, screen);

            // Wallpaper motif: sun + hill, clipped to the screen interior.
            var interior = new Rectangle(screen.X + 2, screen.Y + 2, screen.Width - 3, screen.Height - 3);
            var oldClip = g.Clip;
            g.SetClip(interior);
            g.FillEllipse(darkBrush, screen.X + 3, screen.Y + 2, 5, 5);
            using (var hill = new GraphicsPath())
            {
                hill.AddPolygon(new[]
                {
                    new Point(screen.X, screen.Bottom),
                    new Point(screen.X + 7, screen.Y + 6),
                    new Point(screen.X + 12, screen.Y + 9),
                    new Point(screen.Right, screen.Y + 5),
                    new Point(screen.Right, screen.Bottom),
                });
                g.FillPath(darkBrush, hill);
            }
            g.Clip = oldClip;

            // Stand.
            g.FillRectangle(darkBrush, 15, 21, 2, 3);
            g.FillRectangle(darkBrush, 12, 24, 8, 2);
        }

        return Icon.FromHandle(bmp.GetHicon());
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
