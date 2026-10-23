using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wally.Core;
using Wally.Platform;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Border = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;
using Grid = System.Windows.Controls.Grid;
using Image = System.Windows.Controls.Image;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Cursors = System.Windows.Input.Cursors;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using UserControl = System.Windows.Controls.UserControl;

namespace Wally.App;

/// <summary>
/// The Virtual desktops view. Lists the virtual desktops wally has seen (the
/// documented COM API can only name the current one, so the list grows as the
/// user visits desktops), lets a wallpaper be assigned to each, and applies the
/// assigned wallpaper to all monitors — immediately on demand, and automatically
/// when a desktop switch is detected (the "detect and swap" strategy).
/// </summary>
public sealed class DesktopsView : UserControl, IShellPage
{
    public string PageTitle => "Virtual desktops";

    public FrameworkElement? HeaderActions { get; private set; }

    private const string DragFormat = "wally/image-path";

    private readonly DesktopWallpaperBackend? _backend;
    private readonly VirtualDesktopService? _vds;
    private readonly bool _simulated;

    private readonly List<string> _gallery = new();
    private readonly Dictionary<string, DesktopCard> _cards = new(); // keyed by desktop guid
    private StackPanel? _desktopList;
    private StackPanel? _galleryPanel;
    private ScrollViewer? _galleryScroller;
    private TextBlock? _statusLabel;
    private TextBlock? _toolbarStatus;
    private WallyConfig _config;
    private string _currentGuid = string.Empty;
    private string _selectedGuid = string.Empty;

    public DesktopsView(DesktopWallpaperBackend? backend, VirtualDesktopService? vds, bool simulated)
    {
        _backend = backend;
        _vds = vds;
        _simulated = simulated;

        try { _config = ConfigStore.Load(); }
        catch { _config = new WallyConfig(); }

        Build();

        if (!string.IsNullOrEmpty(_config.GalleryFolder) && Directory.Exists(_config.GalleryFolder))
        {
            LoadGallery(_config.GalleryFolder);
        }

        // Seed the list with the current desktop (if known) and any desktops that
        // already have a saved assignment, so prior work is visible.
        SeedKnownDesktops();
    }

    private sealed class DesktopCard
    {
        public required string Guid { get; init; }
        public required Border Root { get; init; }
        public required Image Preview { get; init; }
        public required TextBlock Caption { get; init; }
        public string? ImagePath { get; set; }
        public int Rotation { get; set; }
        public int Index { get; set; }
    }

    private void Build()
    {
        var root = new Grid { Margin = new Thickness(0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // desktops (hug content)
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // actions (Set wallpaper)
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // gallery (fills rest)

        // Study / Picky toolbar row (row 0). No Save on this page, so no accent
        // primary button: just the icon tools and the folder status on the right.
        _toolbarStatus = PageToolbar.Status();
        var toolbar = PageToolbar.Compose(
            new UIElement[]
            {
                PageToolbar.Tool(
                    "\u27F3", // ⟳
                    "Refresh — re-scan virtual desktops and reload the wallpaper folder",
                    RefreshDesktops),
                PageToolbar.Tool("\u270E", "Choose wallpaper folder…", PickFolder), // ✎
                PageToolbar.Tool(
                    "\u29C9", // ⧉
                    "Open wallpaper folder in Explorer",
                    () =>
                    {
                        if (!PageToolbar.OpenInExplorer(_config.GalleryFolder))
                        {
                            SetStatus("Choose a wallpaper folder first.");
                        }
                    }),
            },
            _toolbarStatus);
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);
        HeaderActions = null;

        // Desktop cards (horizontal strip).
        var listHost = new Border
        {
            Background = Res<Brush>("CardBackground"),
            BorderBrush = Res<Brush>("CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
        };
        var listScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _desktopList = new StackPanel { Orientation = Orientation.Horizontal };
        listScroller.Content = _desktopList;
        listHost.Child = listScroller;
        Grid.SetRow(listHost, 1);
        root.Children.Add(listHost);

        // Actions row: one Set wallpaper button applying the selected desktop's
        // wallpaper to all monitors now (same idea as the Monitors page).
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var rotate = new Button
        {
            Content = "Rotate 90°",
            Style = Res<Style>("SubtleButton"),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Rotate the selected desktop's wallpaper 90° clockwise.",
        };
        rotate.Click += (_, _) => RotateSelected();
        actions.Children.Add(rotate);

        var setWallpaper = new Button
        {
            Content = "Set wallpaper",
            Style = Res<Style>("AccentButton"),
            ToolTip = "Apply the selected desktop's wallpaper to all monitors now.",
        };
        setWallpaper.Click += (_, _) => SetSelectedWallpaper();
        actions.Children.Add(setWallpaper);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        _statusLabel = new TextBlock
        {
            Foreground = Res<Brush>("TextTertiary"),
            FontSize = 11,
            Margin = new Thickness(2, 8, 0, 8),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(_statusLabel, 3);
        root.Children.Add(_statusLabel);

        // Gallery.
        Grid.SetRow(BuildGallery(), 4);
        root.Children.Add(_galleryHostBorder!);

        Content = root;

        if (_backend is null && !_simulated)
        {
            SetStatus("The wallpaper engine is unavailable, so desktop wallpapers can't be applied on this system.");
        }

        if (_vds is null && !_simulated)
        {
            SetStatus("The virtual-desktop service is unavailable on this system.");
        }
    }

    // --- Desktop cards -----------------------------------------------------

    private void SeedKnownDesktops()
    {
        // Any desktop with a saved assignment should be visible immediately.
        foreach (var a in _config.DesktopAssignments)
        {
            EnsureCard(a.DesktopGuid, a.ImagePath);
        }

        // Enumerate the live desktops (via window scanning) and add a card for
        // each — this surfaces desktops the user hasn't assigned yet.
        var guids = _vds?.ListDesktopGuids() ?? Array.Empty<string>();
        foreach (var g in guids)
        {
            EnsureCard(g, _config.AssignmentForDesktop(g)?.ImagePath);
        }

        var current = _vds?.CurrentDesktopGuid() ?? string.Empty;
        if (!string.IsNullOrEmpty(current))
        {
            SetCurrent(current);
        }

        if (_cards.Count == 0)
        {
            SetStatus("No virtual desktops seen yet. Switch to a desktop (Win+Tab) and click Refresh.");
        }
        else
        {
            SetStatus($"{_cards.Count} virtual desktop{(_cards.Count == 1 ? "" : "s")} found.");
        }
    }

    /// <summary>Re-scan virtual desktops and refresh the cards. New desktops are
    /// added; existing cards keep their assignment.</summary>
    private void RefreshDesktops()
    {
        // Pick up images newly added to the wallpaper folder.
        if (!string.IsNullOrEmpty(_config.GalleryFolder) && Directory.Exists(_config.GalleryFolder))
        {
            LoadGallery(_config.GalleryFolder);
        }

        var guids = _vds?.ListDesktopGuids() ?? Array.Empty<string>();
        var before = _cards.Count;
        foreach (var g in guids)
        {
            EnsureCard(g, _config.AssignmentForDesktop(g)?.ImagePath);
        }

        var current = _vds?.CurrentDesktopGuid() ?? string.Empty;
        if (!string.IsNullOrEmpty(current))
        {
            SetCurrent(current);
        }

        var after = _cards.Count;
        SetStatus(after == before
            ? $"Refreshed — {after} virtual desktop{(after == 1 ? "" : "s")}."
            : $"Refreshed — now {after} virtual desktops (was {before}).");
    }

    /// <summary>Add a card for a desktop guid if not already present.</summary>
    private DesktopCard EnsureCard(string guid, string? imagePath)
    {
        if (_cards.TryGetValue(guid, out var existing))
        {
            if (imagePath is not null && existing.ImagePath != imagePath)
            {
                existing.ImagePath = imagePath;
                existing.Rotation = _config.AssignmentForDesktop(guid)?.RotationDegrees ?? 0;
                RenderCardPreview(existing);
            }

            return existing;
        }

        var preview = new Image { Stretch = Stretch.UniformToFill };
        var previewHost = new Border
        {
            Width = 200,
            Height = 120,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Background = Res<Brush>("Subtle"),
            Child = preview,
        };

        var caption = new TextBlock
        {
            Foreground = Res<Brush>("TextSecondary"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(previewHost);
        stack.Children.Add(caption);

        // Hug the content: top-aligned so the card does not stretch to the full
        // list height (which left a tall empty frame below the image).
        var cardRoot = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2),
            BorderBrush = Res<Brush>("CardBorder"),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            AllowDrop = true,
            Cursor = Cursors.Hand,
            Child = stack,
        };

        var card = new DesktopCard
        {
            Guid = guid,
            Root = cardRoot,
            Preview = preview,
            Caption = caption,
            ImagePath = imagePath,
            Rotation = _config.AssignmentForDesktop(guid)?.RotationDegrees ?? 0,
        };

        cardRoot.Drop += (_, e) => OnDropOnDesktop(card, e);
        cardRoot.DragEnter += (_, e) => OnDragOver(cardRoot, e);
        cardRoot.DragOver += (_, e) => OnDragOver(cardRoot, e);
        cardRoot.DragLeave += (_, _) => RefreshCardBorder(card);
        // Click selects the desktop (so Set wallpaper acts on it).
        cardRoot.MouseLeftButtonUp += (_, _) => SelectCard(card);

        _cards[guid] = card;
        _desktopList!.Children.Add(cardRoot);

        var index = _cards.Count;
        card.Index = index;
        caption.Text = $"Desktop {index}";
        RenderCardPreview(card);
        return card;
    }

    private void SelectCard(DesktopCard card)
    {
        _selectedGuid = card.Guid;
        foreach (var c in _cards.Values)
        {
            RefreshCardBorder(c);
        }
    }

    /// <summary>Mark which desktop is current and refresh all card borders.</summary>
    private void SetCurrent(string guid)
    {
        _currentGuid = guid;
        if (string.IsNullOrEmpty(_selectedGuid))
        {
            _selectedGuid = guid; // default selection to the current desktop
        }

        foreach (var c in _cards.Values)
        {
            RefreshCardBorder(c);
        }
    }

    /// <summary>Border colour: accent for the current desktop, a lighter accent
    /// for the selected card, otherwise the neutral card border.</summary>
    private void RefreshCardBorder(DesktopCard card)
    {
        if (card.Guid == _currentGuid)
        {
            card.Root.BorderBrush = Res<Brush>("Accent");
        }
        else if (card.Guid == _selectedGuid)
        {
            card.Root.BorderBrush = Res<Brush>("TextSecondary");
        }
        else
        {
            card.Root.BorderBrush = Res<Brush>("CardBorder");
        }
    }

    private void RenderCardPreview(DesktopCard card)
    {
        if (card.ImagePath is null || !File.Exists(card.ImagePath))
        {
            card.Preview.Source = null;
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 300;
            bmp.UriSource = new Uri(card.ImagePath);
            bmp.Rotation = ToRotation(card.Rotation);
            bmp.EndInit();
            bmp.Freeze();
            card.Preview.Source = bmp;
        }
        catch
        {
            card.Preview.Source = null;
        }
    }

    private void OnDragOver(Border card, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.Copy;
            card.BorderBrush = Res<Brush>("Accent");
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnDropOnDesktop(DesktopCard card, DragEventArgs e)
    {
        card.Root.BorderBrush = Res<Brush>("CardBorder");
        if (!e.Data.GetDataPresent(DragFormat))
        {
            return;
        }

        var path = (string)e.Data.GetData(DragFormat);
        card.ImagePath = path;
        card.Rotation = 0; // a new image starts upright
        RenderCardPreview(card);

        // Persist the assignment for this desktop guid.
        var assignment = new DesktopAssignment(card.Guid, path, WallpaperFit.Fill);
        try
        {
            _config = ConfigStore.Update(c => c.WithDesktopAssignment(assignment));
            SetStatusAccent($"Assigned to {card.Caption.Text}. It will apply when you switch to this desktop.");
        }
        catch (Exception ex)
        {
            SetStatusError($"Could not save assignment: {ex.Message}");
        }

        // If this is the current desktop, apply now too.
        if (_vds?.CurrentDesktopGuid() == card.Guid)
        {
            ApplyToAllMonitors(path);
        }
    }

    /// <summary>
    /// Called by the host when a desktop switch is detected. Ensures a card
    /// exists for the new desktop, highlights it, and applies its assigned
    /// wallpaper (or the auto-assign rotation image for a brand-new desktop).
    /// </summary>
    /// <param name="applyWallpaper">False when the host already applied a saved
    /// monitor setup, which takes priority over the desktop's own wallpaper.</param>
    public void OnDesktopChanged(string guid, bool applyWallpaper = true)
    {
        var assignment = _config.AssignmentForDesktop(guid);
        var imagePath = assignment?.ImagePath;

        // New desktop with no assignment: optionally auto-assign from the pool.
        if (imagePath is null && _config.NewDesktopBehavior == "auto_assign")
        {
            var used = _config.DesktopAssignments.Select(a => a.ImagePath).ToList();
            imagePath = RulesEngine.NextRotationImage(_config, used);
            if (imagePath is not null)
            {
                var auto = new DesktopAssignment(guid, imagePath, WallpaperFit.Fill);
                _config = _config.WithDesktopAssignment(auto);
                try { _config = ConfigStore.Update(c => c.WithDesktopAssignment(auto)); } catch { /* non-fatal */ }
            }
        }

        EnsureCard(guid, imagePath);
        SetCurrent(guid);

        if (!applyWallpaper)
        {
            // A saved monitor setup matched the connected displays and was
            // applied instead; it takes priority over the per-desktop wallpaper.
            SetStatusAccent("Switched desktop — your saved monitor setup takes priority and was applied.");
            return;
        }

        if (imagePath is not null && File.Exists(imagePath))
        {
            ApplyToAllMonitors(imagePath, assignment?.RotationDegrees ?? 0);
            SetStatusAccent($"Switched desktop — applied the assigned wallpaper.");
        }
        else
        {
            SetStatus($"Switched to a desktop with no wallpaper assigned yet ({ShortGuid(guid)}).");
        }
    }

    /// <summary>Apply the selected desktop's assigned wallpaper to all monitors
    /// now (the on-demand companion to the automatic swap on switch).</summary>
    private void SetSelectedWallpaper()
    {
        var guid = string.IsNullOrEmpty(_selectedGuid) ? _currentGuid : _selectedGuid;
        if (string.IsNullOrEmpty(guid) || !_cards.TryGetValue(guid, out var card))
        {
            SetStatus("Select a desktop first.");
            return;
        }

        if (card.ImagePath is null || !File.Exists(card.ImagePath))
        {
            SetStatus($"Desktop {card.Index} has no wallpaper assigned yet — drag one from the gallery.");
            return;
        }

        ApplyToAllMonitors(card.ImagePath, card.Rotation);
        SetStatusAccent($"Applied Desktop {card.Index}'s wallpaper to all monitors.");
    }

    /// <summary>Rotate the selected desktop's wallpaper 90°, save it, and
    /// re-apply when that desktop is the one on screen.</summary>
    private void RotateSelected()
    {
        var guid = string.IsNullOrEmpty(_selectedGuid) ? _currentGuid : _selectedGuid;
        if (string.IsNullOrEmpty(guid) || !_cards.TryGetValue(guid, out var card)
            || card.ImagePath is null || !File.Exists(card.ImagePath))
        {
            SetStatus("Select a desktop with a wallpaper first, then rotate.");
            return;
        }

        card.Rotation = (card.Rotation + 90) % 360;
        RenderCardPreview(card);

        var assignment = new DesktopAssignment(card.Guid, card.ImagePath, WallpaperFit.Fill, card.Rotation);
        try
        {
            _config = ConfigStore.Update(c => c.WithDesktopAssignment(assignment));
            SetStatusAccent($"Rotated Desktop {card.Index} to {card.Rotation}°.");
        }
        catch (Exception ex)
        {
            SetStatusError($"Could not save rotation: {ex.Message}");
        }

        if (_vds?.CurrentDesktopGuid() == card.Guid)
        {
            ApplyToAllMonitors(card.ImagePath, card.Rotation);
        }
    }

    private static Rotation ToRotation(int degrees) => degrees switch
    {
        90 => Rotation.Rotate90,
        180 => Rotation.Rotate180,
        270 => Rotation.Rotate270,
        _ => Rotation.Rotate0,
    };

    /// <summary>
    /// The path to hand Windows: the original when upright, otherwise a rotated
    /// PNG copy baked into %TEMP%\wally (Windows itself cannot rotate wallpaper).
    /// </summary>
    private static string RotatedPathIfNeeded(string sourcePath, int rotation)
    {
        if (rotation % 360 == 0)
        {
            return sourcePath;
        }

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(sourcePath);
        bmp.Rotation = ToRotation(rotation);
        bmp.EndInit();
        bmp.Freeze();

        var dir = Path.Combine(Path.GetTempPath(), "wally");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, $"desk_rot{rotation}_{Path.GetFileNameWithoutExtension(sourcePath)}.png");
        using var fs = new FileStream(outPath, FileMode.Create);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        encoder.Save(fs);
        return outPath;
    }

    private void ApplyToAllMonitors(string imagePath, int rotation = 0)
    {
        if (_simulated || _backend is null)
        {
            SetStatus($"(simulated) would apply {Path.GetFileName(imagePath)} to all monitors.");
            return;
        }

        try
        {
            var pathToApply = RotatedPathIfNeeded(imagePath, rotation);
            var setup = _backend.CurrentSetup();
            foreach (var m in setup.Monitors)
            {
                _backend.SetMonitorWallpaper(m.DeviceId, pathToApply, WallpaperFit.Fill);
            }
        }
        catch (Exception ex)
        {
            SetStatusError($"Could not apply wallpaper: {ex.Message}");
        }
    }

    // --- Gallery (shared layout with the Monitors view) --------------------

    private Border? _galleryHostBorder;

    private Border BuildGallery()
    {
        _galleryHostBorder = new Border
        {
            Background = Res<Brush>("NavBackground"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 8, 0, 0),
        };

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerRow = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.Children.Add(new TextBlock
        {
            Text = "GALLERY",
            Style = Res<Style>("CardLabel"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetRow(headerRow, 0);
        outer.Children.Add(headerRow);

        _galleryScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _galleryScroller.Resources.Add(
            typeof(System.Windows.Controls.Primitives.ScrollBar),
            Res<Style>("SlimScrollBar"));
        _galleryScroller.PreviewMouseWheel += (_, e) =>
        {
            if (_galleryScroller is null) return;
            _galleryScroller.ScrollToHorizontalOffset(_galleryScroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        };

        _galleryPanel = new StackPanel { Orientation = Orientation.Horizontal };
        _galleryPanel.Children.Add(new TextBlock
        {
            Text = "Choose a folder to load wallpapers, then drag them onto a desktop above.",
            Foreground = Res<Brush>("TextTertiary"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _galleryScroller.Content = _galleryPanel;
        Grid.SetRow(_galleryScroller, 1);
        outer.Children.Add(_galleryScroller);

        _galleryHostBorder.Child = outer;
        return _galleryHostBorder;
    }

    private void PickFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a wallpapers folder" };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        LoadGallery(dlg.FolderName);
        try
        {
            var folder = dlg.FolderName;
            _config = ConfigStore.Update(c => c with { GalleryFolder = folder });
        }
        catch { /* non-fatal */ }

        UpdateToolbarStatus();
    }

    private void LoadGallery(string folder)
    {
        _gallery.Clear();
        _galleryPanel!.Children.Clear();

        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder).Where(f => exts.Contains(Path.GetExtension(f)));
        }
        catch (Exception ex)
        {
            _galleryPanel.Children.Add(new TextBlock
            {
                Text = $"Could not read folder: {ex.Message}",
                Foreground = Res<Brush>("Bad"),
                FontSize = 12,
            });
            return;
        }

        foreach (var file in files.Take(200))
        {
            _gallery.Add(file);
            _galleryPanel.Children.Add(BuildThumbnail(file));
        }

        if (_gallery.Count == 0)
        {
            _galleryPanel.Children.Add(new TextBlock
            {
                Text = "No images found in that folder.",
                Foreground = Res<Brush>("TextTertiary"),
                FontSize = 12,
            });
        }

        UpdateToolbarStatus();
    }

    /// <summary>Right-hand toolbar text: the wallpaper folder and its image count.</summary>
    private void UpdateToolbarStatus()
    {
        if (_toolbarStatus is null)
        {
            return;
        }

        _toolbarStatus.Text = PageToolbar.GalleryStatus(_config.GalleryFolder, _gallery.Count);
        _toolbarStatus.ToolTip = _config.GalleryFolder;
    }

    private Border BuildThumbnail(string path)
    {
        Image img;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = 64;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            img = new Image { Source = bmp, Stretch = Stretch.Uniform };
        }
        catch
        {
            img = new Image();
        }

        var thumb = new Border
        {
            Width = 96,
            Height = 60,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 8, 0),
            BorderBrush = Res<Brush>("CardBorder"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            Background = Res<Brush>("Subtle"),
            Child = img,
            ToolTip = Path.GetFileName(path),
        };

        thumb.MouseMove += (_, e) =>
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragDrop.DoDragDrop(thumb, new DataObject(DragFormat, path), DragDropEffects.Copy);
            }
        };

        return thumb;
    }

    // --- helpers -----------------------------------------------------------

    private static string ShortGuid(string guid)
        => guid.Length >= 8 ? guid[..8] : guid;

    private void SetStatus(string text)
    {
        if (_statusLabel is not null) { _statusLabel.Text = text; _statusLabel.Foreground = Res<Brush>("TextTertiary"); }
    }

    private void SetStatusAccent(string text)
    {
        if (_statusLabel is not null) { _statusLabel.Text = text; _statusLabel.Foreground = Res<Brush>("Accent"); }
    }

    private void SetStatusError(string text)
    {
        if (_statusLabel is not null) { _statusLabel.Text = text; _statusLabel.Foreground = Res<Brush>("Bad"); }
    }

    private T Res<T>(string key) => (T)FindResource(key);
}
