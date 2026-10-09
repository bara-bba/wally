using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Wally.Core;
using Wally.Platform;
using Monitor = Wally.Core.Monitor;
// WinForms is enabled project-wide (for the tray NotifyIcon), so disambiguate
// the names this WPF view uses toward their WPF types.
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Grid = System.Windows.Controls.Grid;
using Border = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Wally.App;

/// <summary>
/// The PowerToys-style shell. Hosts the nav rail and swaps content views.
/// The Monitors view is the first real feature: it enumerates connected
/// displays via the verified <see cref="DesktopWallpaperBackend"/> and lets the
/// user pick + apply a wallpaper per monitor.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DesktopWallpaperBackend? _backend;
    private readonly string? _backendError;

    public MainWindow(DesktopWallpaperBackend? backend, string? backendError)
    {
        InitializeComponent();
        _backend = backend;
        _backendError = backendError;
        ShowMonitors();
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

    // --- Monitors view -----------------------------------------------------

    private void ShowMonitors()
    {
        if (ContentHost is null)
        {
            return;
        }

        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = "Monitors", Style = (Style)FindResource("PageTitle") });
        root.Children.Add(new TextBlock
        {
            Text = "Connected displays detected by Windows. Assign a wallpaper to each.",
            Style = (Style)FindResource("PageSubtitle"),
        });

        if (_backend is null)
        {
            root.Children.Add(BuildErrorCard(
                "Could not initialise the wallpaper engine",
                _backendError ?? "The IDesktopWallpaper COM interface is unavailable on this system."));
            ContentHost.Content = root;
            return;
        }

        MonitorSetup setup;
        try
        {
            setup = _backend.CurrentSetup();
        }
        catch (Exception ex)
        {
            root.Children.Add(BuildErrorCard("Could not read the connected monitors", ex.Message));
            ContentHost.Content = root;
            return;
        }

        // Setup summary line.
        root.Children.Add(new TextBlock
        {
            Text = $"{setup.Count} display{(setup.Count == 1 ? "" : "s")}  ·  setup id {setup.Fingerprint}",
            Foreground = (Brush)FindResource("TextTertiary"),
            FontSize = 12,
            Margin = new Thickness(2, 0, 0, 16),
        });

        foreach (var monitor in setup.Monitors)
        {
            root.Children.Add(BuildMonitorCard(monitor));
        }

        ContentHost.Content = root;
    }

    private Border BuildMonitorCard(Monitor monitor)
    {
        var card = new Border { Style = (Style)FindResource("Card") };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Monitor glyph (a small display icon drawn as a rounded rect).
        var glyph = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("AccentMuted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
            Child = new TextBlock
            {
                Text = "\U0001F5A5",
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(glyph, 0);
        grid.Children.Add(glyph);

        // Monitor details.
        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock
        {
            Text = monitor.FriendlyName,
            Foreground = (Brush)FindResource("TextPrimary"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (monitor.IsPrimary)
        {
            nameRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = (Brush)FindResource("Accent"),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "PRIMARY", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0, 0x10, 0x18)) },
            });
        }
        details.Children.Add(nameRow);
        details.Children.Add(new TextBlock
        {
            Text = $"{monitor.Resolution}  ·  position ({monitor.X}, {monitor.Y})",
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
        });
        var pathPreview = new TextBlock
        {
            Foreground = (Brush)FindResource("TextTertiary"),
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "No wallpaper selected",
        };
        details.Children.Add(pathPreview);
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);

        // Actions.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var browse = new Button { Content = "Browse…", Style = (Style)FindResource("SubtleButton"), Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Set wallpaper", Style = (Style)FindResource("AccentButton"), IsEnabled = false };

        string? selectedPath = null;
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Title = $"Choose a wallpaper for {monitor.FriendlyName}",
                Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*",
            };
            if (dlg.ShowDialog() == true)
            {
                selectedPath = dlg.FileName;
                pathPreview.Text = selectedPath;
                apply.IsEnabled = true;
            }
        };

        apply.Click += (_, _) =>
        {
            if (selectedPath is null)
            {
                return;
            }

            try
            {
                _backend!.SetMonitorWallpaper(monitor.DeviceId, selectedPath, WallpaperFit.Fill);
                pathPreview.Text = $"Applied: {selectedPath}";
                pathPreview.Foreground = (Brush)FindResource("Accent");
            }
            catch (Exception ex)
            {
                pathPreview.Text = $"Failed: {ex.Message}";
                pathPreview.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            }
        };

        actions.Children.Add(browse);
        actions.Children.Add(apply);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    // --- Shared helpers ----------------------------------------------------

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

    private Border BuildErrorCard(string title, string detail)
    {
        var card = new Border { Style = (Style)FindResource("Card") };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        card.Child = panel;
        return card;
    }
}
