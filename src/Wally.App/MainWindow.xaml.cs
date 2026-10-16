using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wally.Platform;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Border = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;

namespace Wally.App;

/// <summary>
/// The PowerToys-style shell. Hosts the nav rail and swaps content views.
/// Monitors is delegated to <see cref="MonitorsView"/>; the other sections are
/// honest placeholders describing what is coming.
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DesktopWallpaperBackend? _backend;
    private readonly string? _backendError;
    private readonly bool _simulate;
    private readonly VirtualDesktopService? _vds;
    private MonitorsView? _monitorsView;
    private DesktopsView? _desktopsView;
    private DesktopWatcher? _desktopWatcher;
    private bool _navExpanded;
    private System.Drawing.Icon? _smallIcon;
    private System.Drawing.Icon? _largeIcon;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether the nav rail shows labels (expanded) or icons only (collapsed).</summary>
    public bool NavExpanded
    {
        get => _navExpanded;
        set
        {
            if (_navExpanded == value)
            {
                return;
            }

            _navExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NavExpanded)));
        }
    }

    public MainWindow(DesktopWallpaperBackend? backend, string? backendError, bool simulate = false, VirtualDesktopService? vds = null)
    {
        InitializeComponent();
        DwmHelper.ApplyPowerToysChrome(this);
        Title = AppInfo.NameWithVersion;
        TaglineText.Text = $"Per-monitor and per-desktop wallpapers · v{AppInfo.Version}";
        Icon = BrandMark.ToImageSource(32);
        DataContext = this;
        _backend = backend;
        _backendError = backendError;
        _simulate = simulate;
        _vds = vds;
        ShowMonitors();

        // Once the native window handle exists, track it (fallback for desktop
        // resolution) and begin watching for virtual-desktop switches.
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        Loaded += (_, _) => UpdateMaximizeGlyph();
        AccentTheme.Changed += OnAccentChanged;
        Closed += (_, _) =>
        {
            AccentTheme.Changed -= OnAccentChanged;
            _desktopWatcher?.Dispose();
            _smallIcon?.Dispose();
            _largeIcon?.Dispose();
        };
    }

    // Keep the caption-strip maximize button's glyph and tooltip in sync with
    // the window state: a "maximize" square when normal, a "restore" overlap
    // when maximized. The glyph lives inside the button's ControlTemplate, so
    // it is reached via the template once the button is loaded.
    private void UpdateMaximizeGlyph()
    {
        if (MaximizeButton is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "□";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    // Custom caption-strip controls (the OS titlebar is removed via WindowChrome).
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    // Toggle maximize/restore from the caption-strip button.
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    // Dragging the caption strip moves the window; a double-click toggles
    // maximize/restore, matching a normal titlebar. With WindowChrome's
    // CaptionHeight=0 the strip provides the move region itself.
    private void CaptionStrip_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        // Dragging a maximized window restores it and continues the move under
        // the cursor, like a normal titlebar. All math stays in device-
        // independent units (DIPs): mixing PointToScreen's physical pixels with
        // Left/Top/RestoreBounds (DIPs) on a scaled display puts the window off
        // under the cursor, which is why the grab point fell outside the frame.
        if (WindowState == WindowState.Maximized)
        {
            var cursorInWindow = e.GetPosition(this);          // DIPs, from window top-left
            double ratioX = ActualWidth > 0 ? cursorInWindow.X / ActualWidth : 0.5;

            double restoredWidth = RestoreBounds.Width;
            if (double.IsNaN(restoredWidth) || restoredWidth <= 0)
            {
                restoredWidth = Width; // fallback to the configured width
            }

            // Cursor position on the virtual desktop in DIPs. PointToScreen gives
            // PHYSICAL pixels; divide by the per-monitor DPI scale to get DIPs so
            // the restored Left/Top (which are DIPs) line up under the cursor on
            // a scaled display. Doing this wrong is what pushed the grab point
            // outside the restored frame.
            var src = System.Windows.PresentationSource.FromVisual(this);
            double scaleX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double scaleY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            var cursorPhysical = PointToScreen(cursorInWindow);
            double cursorScreenX = cursorPhysical.X / scaleX;
            double cursorScreenY = cursorPhysical.Y / scaleY;

            WindowState = WindowState.Normal;

            // Place the restored window so the cursor keeps the same proportional
            // X on the (now narrower) title bar, and sits ~15 DIP below the top.
            Left = cursorScreenX - restoredWidth * ratioX;
            Top = cursorScreenY - 15;
        }

        DragMove();
    }

    // Close hides to the tray (the tray's Closing handler cancels the real exit);
    // the app only truly quits from the tray menu's Quit.
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        // Set the window icon natively. A borderless WindowChrome window does not
        // reliably publish Window.Icon to the taskbar button, and WPF's icon can
        // lose to a stale OS icon cache; WM_SETICON on the HWND is always honored.
        SetWindowIconNative(hwnd);

        if (_vds is null || _simulate)
        {
            return;
        }

        _vds.TrackWindow(hwnd);

        _desktopWatcher = new DesktopWatcher(_vds);
        _desktopWatcher.DesktopChanged += OnDesktopChanged;
        _desktopWatcher.Start();

        // Apply the current desktop's assigned wallpaper immediately on launch,
        // so the correct wallpaper is shown without waiting for the first switch.
        // The watcher seeds its guid silently (no startup "change" event), so we
        // drive the initial apply explicitly here.
        var current = _vds.CurrentDesktopGuid();
        if (!string.IsNullOrEmpty(current))
        {
            OnDesktopChanged(current);
        }
    }

    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void SetWindowIconNative(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Separate HICONs sized for the small (titlebar/taskbar) and large
        // (Alt-Tab) slots so each is crisp rather than a scaled 32px bitmap.
        // Held as fields so the GDI handles live as long as the window uses them;
        // the previous pair is released after the new one is in place.
        var oldSmall = _smallIcon;
        var oldLarge = _largeIcon;
        _smallIcon = BrandMark.ToIcon(16);
        _largeIcon = BrandMark.ToIcon(32);
        SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, _smallIcon.Handle);
        SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, _largeIcon.Handle);
        oldSmall?.Dispose();
        oldLarge?.Dispose();
    }

    // Redraw the taskbar / Alt-Tab icon in the new accent colour (Study does the same).
    private void OnAccentChanged()
    {
        Icon = BrandMark.ToImageSource(32);
        SetWindowIconNative(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    private void OnDesktopChanged(string guid)
    {
        // Ensure the view exists so the swap is applied even if the user hasn't
        // opened the Desktops page yet (auto-apply must work in the background).
        EnsureDesktopsView();

        // Priority: a saved monitor setup matching the connected displays beats
        // the per-desktop wallpaper. Previously the desktop wallpaper was applied
        // after the setup and overwrote it.
        var setupApplied = _monitorsView?.ApplySavedSetupIfAny() ?? false;
        _desktopsView!.OnDesktopChanged(guid, applyWallpaper: !setupApplied);
    }

    private void NavMonitors_Checked(object sender, RoutedEventArgs e) => ShowMonitors();

    // Swap the page into the content host and publish its title + header
    // actions to the shell header.
    private void ShowPage(FrameworkElement page)
    {
        ContentHost.Content = page;
        if (page is IShellPage shellPage)
        {
            PageTitleText.Text = shellPage.PageTitle;
            PageActionsHost.Content = shellPage.HeaderActions;
        }
        else
        {
            PageTitleText.Text = string.Empty;
            PageActionsHost.Content = null;
        }
    }

    private void NavDesktops_Checked(object sender, RoutedEventArgs e) => ShowDesktops();

    private void ShowDesktops()
    {
        if (ContentHost is null)
        {
            return;
        }

        EnsureDesktopsView();
        ShowPage(_desktopsView!);
    }

    private void EnsureDesktopsView()
    {
        _desktopsView ??= new DesktopsView(_backend, _vds, _simulate);
    }

    private void NavSettings_Checked(object sender, RoutedEventArgs e) => ShowSettings();

    private SettingsView? _settingsView;

    private void ShowSettings()
    {
        if (ContentHost is null)
        {
            return;
        }

        _settingsView ??= new SettingsView();
        ShowPage(_settingsView!);
    }

    private void ShowMonitors()
    {
        if (ContentHost is null)
        {
            return;
        }

        // Reuse the view so gallery + assignments survive nav switches.
        _monitorsView ??= new MonitorsView(
            _backend,
            _backendError,
            _simulate ? FakeMonitorProvider.TwoPlusDisplays() : null);
        ShowPage(_monitorsView!);
    }
}
