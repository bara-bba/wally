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
    private MonitorsView? _monitorsView;
    private bool _navExpanded;

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

    public MainWindow(DesktopWallpaperBackend? backend, string? backendError, bool simulate = false)
    {
        InitializeComponent();
        DataContext = this;
        _backend = backend;
        _backendError = backendError;
        _simulate = simulate;
        ShowMonitors();
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

    private void NavDesktops_Checked(object sender, RoutedEventArgs e) =>
        ShowPlaceholder(
            "Virtual desktops",
            "A dedicated wallpaper per Windows virtual desktop.",
            "Coming next: the virtual-desktop service (pinned to Windows 11 build 26100) that detects desktop switches and swaps the wallpaper automatically.");

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
