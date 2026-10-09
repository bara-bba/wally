using System.Windows;
using Wally.Platform;

namespace Wally.App;

/// <summary>
/// Application entry. Creates the backend and the main window. If COM
/// initialisation fails (e.g. an unexpected environment) the window still opens
/// and surfaces the error rather than crashing on launch.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        window.Show();
    }
}
