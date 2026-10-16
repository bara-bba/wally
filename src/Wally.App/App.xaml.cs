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

        // Apply the saved accent colour before the first window paints, so the
        // UI starts in the user's chosen colour rather than flashing the default.
        try
        {
            var cfg = Wally.Core.ConfigStore.Load();
            if (AccentTheme.TryParse(cfg.AccentColor, out var accent))
            {
                AccentTheme.Apply(accent);
            }
            else
            {
                AccentTheme.Apply(AccentTheme.Default);
            }
        }
        catch
        {
            AccentTheme.Apply(AccentTheme.Default);
        }

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

        // Virtual-desktop service is best-effort: if the documented COM class is
        // unavailable, the Desktops view degrades to an informational message.
        VirtualDesktopService? vds = null;
        try
        {
            vds = new VirtualDesktopService();
        }
        catch
        {
            vds = null;
        }

        var window = new MainWindow(backend, backendError, e.Args.Contains("--simulate"), vds);
        _tray = new TrayIcon(window);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
