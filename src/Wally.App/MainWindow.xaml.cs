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
    private const double NavCollapsedWidth = 60;
    private const double NavExpandedWidth = 220;

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
        Title = AppInfo.NameWithVersion;
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
        Closed += (_, _) =>
        {
            _desktopWatcher?.Dispose();
            _smallIcon?.Dispose();
            _largeIcon?.Dispose();
        };
    }

    // Custom caption-strip controls (the OS titlebar is removed via WindowChrome).
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

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
        // Held as fields so the GDI handles live as long as the window uses them.
        _smallIcon = BrandMark.ToIcon(16);
        _largeIcon = BrandMark.ToIcon(32);
        SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, _smallIcon.Handle);
        SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, _largeIcon.Handle);
    }

    private void OnDesktopChanged(string guid)
    {
        // Ensure the view exists so the swap is applied even if the user hasn't
        // opened the Desktops page yet (auto-apply must work in the background).
        EnsureDesktopsView();
        _desktopsView!.OnDesktopChanged(guid);
    }

    private void NavToggle_Click(object sender, RoutedEventArgs e)
    {
        NavExpanded = !NavExpanded;
        var to = NavExpanded ? NavExpandedWidth : NavCollapsedWidth;
        var anim = new GridLengthAnimation
        {
            From = new GridLength(NavColumn.ActualWidth),
            To = new GridLength(to),
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
        };
        NavColumn.BeginAnimation(ColumnDefinition.WidthProperty, anim);
    }

    private void NavMonitors_Checked(object sender, RoutedEventArgs e) => ShowMonitors();

    private void NavDesktops_Checked(object sender, RoutedEventArgs e) => ShowDesktops();

    private void ShowDesktops()
    {
        if (ContentHost is null)
        {
            return;
        }

        EnsureDesktopsView();
        ContentHost.Content = _desktopsView;
    }

    private void EnsureDesktopsView()
    {
        _desktopsView ??= new DesktopsView(_backend, _vds, _simulate);
    }

    private void NavSettings_Checked(object sender, RoutedEventArgs e) =>
        ShowPlaceholder(
            "Settings",
            "Startup behaviour, new-desktop rules, and the image rotation pool.",
            "Coming next: start-on-login, the new-desktop behaviour toggle (open the manager vs auto-assign), and the rotation pool editor.");

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
        ContentHost.Content = _monitorsView;
    }

    private void ShowPlaceholder(string title, string subtitle, string body)
    {
        if (ContentHost is null)
        {
            return;
        }

        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("PageTitle") });
        root.Children.Add(new TextBlock { Text = subtitle, Style = (Style)FindResource("PageSubtitle") });

        var card = new Border { Style = (Style)FindResource("Card") };
        card.Child = new TextBlock
        {
            Text = body,
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        };
        root.Children.Add(card);
        ContentHost.Content = root;
    }
}
