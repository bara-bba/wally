using System.Windows;
using Wally.Platform;
using Application = System.Windows.Application;

namespace Wally.App;

/// <summary>
/// Application entry. Creates the backend, the main window, and the tray icon
/// that keeps wally running in the background. ShutdownMode is explicit so
/// hiding the window to the tray does not end the process — only the tray's
/// "Quit" does.
/// </summary>
public partial class App : Application
{
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Do not quit when the last window hides; the tray owns the lifetime.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DesktopWallpaperBackend? backend = null;
        string? backendError = null;
        try
        {
            backend = new DesktopWallpaperBackend();
        }
        catch (Exception ex)
        {
            backendError = ex.Message;
        }

        var window = new MainWindow(backend, backendError);
        _tray = new TrayIcon(window);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
