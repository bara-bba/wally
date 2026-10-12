using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wally.Core;
using Wally.Platform;
using Monitor = Wally.Core.Monitor;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Grid = System.Windows.Controls.Grid;
using Border = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Panel = System.Windows.Controls.Panel;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using DataObject = System.Windows.DataObject;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Cursors = System.Windows.Input.Cursors;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Wally.App;

/// <summary>
/// The Monitors view. Lays out connected displays the way Windows Settings does
/// (positioned + scaled by real X/Y and resolution), previews the chosen
/// wallpaper inside each screen, advises on low resolution, allows a 90° rotate,
/// and offers a draggable gallery strip along the bottom. Images are dragged
/// from the gallery onto a monitor to assign them.
/// </summary>
public sealed class MonitorsView : UserControl
{
    private const string DragFormat = "wally/image-path";

    private readonly DesktopWallpaperBackend? _backend;
    private readonly string? _backendError;
    private readonly IMonitorProvider? _monitorOverride;
    private readonly bool _simulated;

    // Per-monitor UI state, keyed by device id.
    private readonly Dictionary<string, MonitorSlot> _slots = new();
    private readonly List<string> _gallery = new();
    private WrapOrStack? _galleryPanel;
    private Canvas? _layoutCanvas;
    private StackPanel? _controlsPanel;
    private StackPanel? _barButtons;
    private TextBlock? _statusLabel;
    private ScrollViewer? _galleryScroller;
    private WallyConfig _config;
    private MonitorSetup? _currentSetup;

    public MonitorsView(DesktopWallpaperBackend? backend, string? backendError, IMonitorProvider? monitorOverride = null)
    {
        _backend = backend;
        _backendError = backendError;
        _monitorOverride = monitorOverride;
        _simulated = monitorOverride is not null;

        try
        {
            _config = ConfigStore.Load();
        }
        catch
        {
            _config = new WallyConfig();
        }

        Build();

        // Restore the previously chosen gallery folder, if it still exists.
        if (!string.IsNullOrEmpty(_config.GalleryFolder) && Directory.Exists(_config.GalleryFolder))
        {
            LoadGallery(_config.GalleryFolder);
        }

        // If this exact set of monitors was saved before, apply it to the live
        // desktop now (previews were already loaded during Build()).
        AutoApplySavedSetup();

        // Auto-detect monitors being plugged in / removed while wally runs.
        // Only for the real backend — the simulated provider is fixed.
        if (!_simulated)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Unloaded += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Windows raises this event several times during a reconfiguration, and
        // mid-sequence the COM query can briefly return ZERO monitors. Refreshing
        // on that transient would wipe the layout to nothing ("now 0 displays").
        // So probe first and only act on a non-empty read; retry a few times for
        // the empty case before giving up.
        Dispatcher.BeginInvoke(new Action(() => RefreshAfterDisplayChange(attempt: 0)));
    }

    private void RefreshAfterDisplayChange(int attempt)
    {
        const int maxAttempts = 6;      // ~3s total
        const int retryDelayMs = 500;

        IMonitorProvider? provider = _monitorOverride ?? _backend;
        if (provider is null)
        {
            return;
        }

        int count;
        try
        {
            count = provider.CurrentSetup().Count;
        }
        catch
        {
            count = 0;
        }

        if (count == 0 && attempt < maxAttempts)
        {
            // Transient empty read — wait and try again on the UI thread.
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(retryDelayMs),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                RefreshAfterDisplayChange(attempt + 1);
            };
            timer.Start();
            return;
        }

        RefreshMonitors();
        AutoApplySavedSetup();
    }

    private sealed class MonitorSlot
    {
        public required Monitor Monitor { get; init; }
        public required Border ScreenBorder { get; init; }
        public required Image PreviewImage { get; init; }
        public string? ImagePath { get; set; }
        public int RotationDegrees { get; set; }
        public ImageQuality Quality { get; set; } = ImageQuality.Good;

        // The monitor's button in the single bottom bar.
        public Border? BarButton { get; set; }
        public bool Selected { get; set; }
    }

    // A StackPanel used as a horizontal gallery row (kept as a field type alias).
    private sealed class WrapOrStack : StackPanel { }

    private void Build()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 180 }); // layout + controls
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // splitter
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.4, GridUnitType.Star), MinHeight = 150 }); // gallery (resizable, larger)

        // Header: title/subtitle on the left, a Refresh button on the right.
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headerText.Children.Add(new TextBlock { Text = "Monitors", Style = Res<Style>("PageTitle") });
        headerText.Children.Add(new TextBlock
        {
            Text = "Drag an image from the gallery onto a display. Arranged like Windows display settings.",
            Style = Res<Style>("PageSubtitle"),
        });
        Grid.SetColumn(headerText, 0);
        header.Children.Add(headerText);

        // Right side: Save setup + Refresh.
        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var saveSetup = new Button
        {
            Content = "Save setup",
            Style = Res<Style>("SubtleButton"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Remember the current wallpaper on each display for this exact set of monitors. "
                    + "When this set is detected again, wally re-applies it automatically.",
        };
        saveSetup.Click += (_, _) => SaveCurrentSetup();
        headerActions.Children.Add(saveSetup);

        var refresh = new Button
        {
            Style = Res<Style>("SubtleButton"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Re-read connected displays (use after plugging in or removing a monitor)",
        };
        // Compose icon + label as separate runs so the Fluent glyph never
        // bleeds into the text font (the earlier mixed-font string showed a
        // tofu box). Icon in the symbol font, label in the UI font.
        var refreshContent = new StackPanel { Orientation = Orientation.Horizontal };
        refreshContent.Children.Add(new TextBlock
        {
            Text = "\uE72C",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        refreshContent.Children.Add(new TextBlock
        {
            Text = "Refresh",
            VerticalAlignment = VerticalAlignment.Center,
        });
        refresh.Content = refreshContent;
        refresh.Click += (_, _) => RefreshMonitors();
        headerActions.Children.Add(refresh);

        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Layout + per-monitor controls share row 1: canvas fills, controls dock bottom.
        var layoutHost = new Border
        {
            Background = Res<Brush>("CardBackground"),
            BorderBrush = Res<Brush>("CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            MinHeight = 180,
        };
        var layoutDock = new DockPanel { LastChildFill = true };
        _controlsPanel = new StackPanel();
        DockPanel.SetDock(_controlsPanel, Dock.Bottom);
        layoutDock.Children.Add(_controlsPanel);

        _layoutCanvas = new Canvas { ClipToBounds = true, MinHeight = 150 };
        _layoutCanvas.SizeChanged += (_, _) => LayoutMonitors();
        layoutDock.Children.Add(_layoutCanvas);

        layoutHost.Child = layoutDock;
        Grid.SetRow(layoutHost, 1);
        root.Children.Add(layoutHost);

        // Draggable splitter so the gallery area can be grown/shrunk.
        var splitter = new GridSplitter
        {
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Rows,
            Cursor = Cursors.SizeNS,
        };
        Grid.SetRow(splitter, 2);
        root.Children.Add(splitter);

        // Gallery strip (row 3, resizable).
        Grid.SetRow(BuildGallery(), 3);
        root.Children.Add(_galleryHost!);

        Content = root;

        if (_backend is null && _monitorOverride is null)
        {
            _layoutCanvas.Children.Add(new TextBlock
            {
                Text = _backendError ?? "The wallpaper engine is unavailable on this system.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
                Margin = new Thickness(4),
            });
            return;
        }

        BuildMonitorSlots();
    }

    // --- Monitor layout ----------------------------------------------------

    private void BuildMonitorSlots()
    {
        _slots.Clear();

        IMonitorProvider? provider = _monitorOverride ?? _backend;
        if (provider is null)
        {
            return;
        }

        MonitorSetup setup;
        try
        {
            setup = provider.CurrentSetup();
            _currentSetup = setup;
        }
        catch (Exception ex)
        {
            _layoutCanvas!.Children.Add(new TextBlock
            {
                Text = $"Could not read monitors: {ex.Message}",
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
            });
            return;
        }

        foreach (var m in setup.Monitors)
        {
            var preview = new Image
            {
                Stretch = Stretch.UniformToFill,
                Visibility = Visibility.Collapsed,
            };

            var label = new TextBlock
            {
                Text = m.IsPrimary ? $"{m.FriendlyName}  ·  primary" : m.FriendlyName,
                Foreground = Res<Brush>("TextPrimary"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(6),
            };

            // Clean screen: just the preview and the name. No icons/bars/dots on it.
            var content = new Grid();
            content.Children.Add(preview);
            content.Children.Add(label);

            var border = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = m.IsPrimary ? Res<Brush>("AccentMuted") : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                BorderBrush = Res<Brush>("CardBorder"),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                AllowDrop = true,
                Cursor = Cursors.Hand,
                Child = content,
            };

            var slot = new MonitorSlot
            {
                Monitor = m,
                ScreenBorder = border,
                PreviewImage = preview,
            };
            _slots[m.DeviceId] = slot;

            border.Drop += (_, e) => OnDropOnMonitor(slot, e);
            border.DragEnter += (_, e) => OnDragOverMonitor(border, e);
            border.DragOver += (_, e) => OnDragOverMonitor(border, e);
            border.DragLeave += (_, _) => border.BorderBrush = Res<Brush>("CardBorder");

            _layoutCanvas!.Children.Add(border);
        }

        BuildBottomBar();

        // If this exact set of monitors has a saved configuration, load it into
        // the previews so the UI reflects what will be applied.
        LoadSavedSetupIntoSlots();

        LayoutMonitors();
    }

    /// <summary>
    /// Re-read the connected displays and rebuild the layout + bottom bar. Call
    /// this after a monitor is plugged in or removed while wally is running —
    /// the display set is otherwise only read once at startup. Existing image
    /// assignments are preserved for displays that are still present (matched by
    /// device id).
    /// </summary>
    private void RefreshMonitors()
    {
        if (_layoutCanvas is null)
        {
            return;
        }

        var before = _slots.Count;

        // Probe the new setup first. If it reports zero displays but we currently
        // have some, this is almost certainly a transient read during a monitor
        // reconfiguration — keep the existing layout rather than wiping it.
        IMonitorProvider? provider = _monitorOverride ?? _backend;
        if (provider is not null && before > 0)
        {
            int probed;
            try { probed = provider.CurrentSetup().Count; }
            catch { probed = 0; }

            if (probed == 0)
            {
                SetStatus("No displays reported right now — keeping the current layout.");
                return;
            }
        }

        // Remember current assignments by device id so still-present displays
        // keep their wallpaper/rotation across the rebuild.
        var previous = _slots.Values
            .Where(s => s.ImagePath is not null)
            .ToDictionary(s => s.Monitor.DeviceId, s => (s.ImagePath!, s.RotationDegrees));

        _layoutCanvas.Children.Clear();
        _controlsPanel?.Children.Clear();

        BuildMonitorSlots();
        var after = _slots.Count;

        // Reapply assignments to displays that survived the refresh.
        foreach (var slot in _slots.Values)
        {
            if (previous.TryGetValue(slot.Monitor.DeviceId, out var prev))
            {
                slot.ImagePath = prev.Item1;
                slot.RotationDegrees = prev.Item2;
                RenderPreview(slot);
            }
        }

        SetStatus(after == before
            ? $"Refreshed — {after} display{(after == 1 ? "" : "s")} detected."
            : $"Refreshed — now {after} display{(after == 1 ? "" : "s")} (was {before}).");
    }

    // --- Saved per-setup configurations -----------------------------------

    /// <summary>
    /// Save the current per-monitor wallpapers as the configuration for THIS
    /// exact set of monitors (keyed by the setup's fingerprint, which is derived
    /// from the monitors' hardware ids and is order-independent). When this same
    /// set of monitors is detected again, wally re-applies these wallpapers
    /// automatically.
    /// </summary>
    private void SaveCurrentSetup()
    {
        if (_currentSetup is null)
        {
            SetStatus("No displays detected to save.");
            return;
        }

        var assigned = _slots.Values.Where(s => s.ImagePath is not null).ToList();
        if (assigned.Count == 0)
        {
            SetStatus("Assign a wallpaper to at least one display before saving.");
            return;
        }

        var rule = new SetupRule(_currentSetup.Fingerprint);
        foreach (var slot in assigned)
        {
            rule = rule.WithMonitor(new MonitorRule(
                EdidKey: slot.Monitor.EdidKey,
                ImagePath: slot.ImagePath!,
                Fit: WallpaperFit.Fill,
                RotationDegrees: slot.RotationDegrees));
        }

        _config = _config.WithSetupRule(rule);
        try
        {
            ConfigStore.Save(_config);
            SetStatusAccent($"Saved this {_currentSetup.Count}-display setup — it will auto-apply when detected again.");
        }
        catch (Exception ex)
        {
            SetStatusError($"Could not save setup: {ex.Message}");
        }
    }

    /// <summary>
    /// If the current set of monitors has a saved configuration, load each
    /// monitor's saved wallpaper into its preview. Does not touch the live
    /// desktop — that is done by <see cref="AutoApplySavedSetup"/>.
    /// </summary>
    private void LoadSavedSetupIntoSlots()
    {
        if (_currentSetup is null)
        {
            return;
        }

        var rule = _config.RuleForSetup(_currentSetup.Fingerprint);
        if (rule is null)
        {
            return;
        }

        foreach (var slot in _slots.Values)
        {
            var mr = rule.RuleFor(slot.Monitor.EdidKey);
            if (mr is not null && File.Exists(mr.ImagePath))
            {
                slot.ImagePath = mr.ImagePath;
                slot.RotationDegrees = mr.RotationDegrees;
                RenderPreview(slot);
            }
        }
    }

    /// <summary>
    /// If the current set of monitors has a saved configuration, apply those
    /// wallpapers to the real desktop now. Called once at startup. No-op in
    /// simulated mode or when no rule matches.
    /// </summary>
    private void AutoApplySavedSetup()
    {
        if (_simulated || _backend is null || _currentSetup is null)
        {
            return;
        }

        var rule = _config.RuleForSetup(_currentSetup.Fingerprint);
        if (rule is null)
        {
            return;
        }

        var applied = 0;
        foreach (var slot in _slots.Values)
        {
            var mr = rule.RuleFor(slot.Monitor.EdidKey);
            if (mr is null || !File.Exists(mr.ImagePath))
            {
                continue;
            }

            try
            {
                var path = mr.RotationDegrees % 360 != 0
                    ? WriteRotatedCopy(mr.ImagePath, mr.RotationDegrees)
                    : mr.ImagePath;
                _backend.SetMonitorWallpaper(slot.Monitor.DeviceId, path, mr.Fit);
                applied++;
            }
            catch
            {
                // Best-effort auto-apply; a failure on one monitor is non-fatal.
            }
        }

        if (applied > 0)
        {
            SetStatusAccent($"Recognised this setup — auto-applied saved wallpapers to {applied} display(s).");
        }
    }

    /// <summary>
    /// The single bottom bar under the whole preview area: one monitor
    /// icon-button per display (border turns yellow/red to warn on resolution,
    /// white when selected), a Rotate button acting on the selected display, and
    /// one Set wallpaper button applying to every assigned display.
    /// </summary>
    private void BuildBottomBar()
    {
        if (_controlsPanel is null)
        {
            return;
        }

        _controlsPanel.Children.Clear();
        _controlsPanel.Margin = new Thickness(0, 12, 0, 0);

        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // monitor buttons
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // actions

        // Left: one monitor icon-button per display.
        _barButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var slot in _slots.Values)
        {
            var icon = new TextBlock
            {
                Text = "\uE7F4", // monitor glyph
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Res<Brush>("TextSecondary"),
            };

            // Each button is a 2px border (the resolution-warning ring) wrapping the icon.
            var button = new Border
            {
                Width = 46,
                Height = 38,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(2),
                BorderBrush = Res<Brush>("CardBorder"),
                Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Child = icon,
                ToolTip = slot.Monitor.IsPrimary ? $"{slot.Monitor.FriendlyName} · primary" : slot.Monitor.FriendlyName,
            };

            slot.BarButton = button;
            button.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SelectMonitor(slot);
            };

            _barButtons.Children.Add(button);
        }

        Grid.SetColumn(_barButtons, 0);
        bar.Children.Add(_barButtons);

        // Right: Rotate (selected) + Set wallpaper (all).
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var rotate = new Button
        {
            Content = "Rotate 90°",
            Style = Res<Style>("SubtleButton"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        rotate.Click += (_, _) => RotateSelected();
        actions.Children.Add(rotate);

        var apply = new Button
        {
            Content = "Set wallpaper",
            Style = Res<Style>("AccentButton"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        apply.Click += (_, _) => ApplyAll();
        actions.Children.Add(apply);

        Grid.SetColumn(actions, 1);
        bar.Children.Add(actions);

        _controlsPanel.Children.Add(bar);

        _statusLabel = new TextBlock
        {
            Foreground = Res<Brush>("TextTertiary"),
            FontSize = 11,
            Margin = new Thickness(2, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _controlsPanel.Children.Add(_statusLabel);
    }

    /// <summary>Position + scale each monitor rectangle to mirror the real desktop
    /// arrangement, fitting the whole set inside the visible area.</summary>
    private void LayoutMonitors()
    {
        if (_layoutCanvas is null || _slots.Count == 0)
        {
            return;
        }

        var cw = _layoutCanvas.ActualWidth;
        var ch = _layoutCanvas.ActualHeight;
        if (cw < 10 || ch < 10)
        {
            return;
        }

        var monitors = _slots.Values.Select(s => s.Monitor).ToList();
        var minX = monitors.Min(m => m.X);
        var minY = monitors.Min(m => m.Y);
        var maxX = monitors.Max(m => m.X + m.Width);
        var maxY = monitors.Max(m => m.Y + m.Height);

        var spanX = Math.Max(1, maxX - minX);
        var spanY = Math.Max(1, maxY - minY);

        // Fit the whole arrangement inside the canvas, preserving aspect — every
        // display stays visible within the size (no scrolling).
        const double pad = 20;
        var scale = Math.Min((cw - 2 * pad) / spanX, (ch - 2 * pad) / spanY);
        var offsetX = (cw - spanX * scale) / 2;
        var offsetY = (ch - spanY * scale) / 2;

        foreach (var slot in _slots.Values)
        {
            var m = slot.Monitor;
            var w = m.Width * scale;
            var h = m.Height * scale;
            var left = offsetX + (m.X - minX) * scale;
            var top = offsetY + (m.Y - minY) * scale;

            const double gap = 4;
            slot.ScreenBorder.Width = Math.Max(40, w - gap);
            slot.ScreenBorder.Height = Math.Max(40, h - gap);
            Canvas.SetLeft(slot.ScreenBorder, left + gap / 2);
            Canvas.SetTop(slot.ScreenBorder, top + gap / 2);
        }
    }

    // --- Drag & drop from gallery -----------------------------------------

    private void OnDragOverMonitor(Border border, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.Copy;
            border.BorderBrush = Res<Brush>("Accent");
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnDropOnMonitor(MonitorSlot slot, DragEventArgs e)
    {
        slot.ScreenBorder.BorderBrush = Res<Brush>("CardBorder");
        if (!e.Data.GetDataPresent(DragFormat))
        {
            return;
        }

        var path = (string)e.Data.GetData(DragFormat);
        AssignImage(slot, path);
    }

    private void AssignImage(MonitorSlot slot, string path, int rotation = 0)
    {
        slot.ImagePath = path;
        slot.RotationDegrees = rotation;
        RenderPreview(slot);
        SelectMonitor(slot); // selecting the just-dropped monitor is the natural focus
    }

    private void RenderPreview(MonitorSlot slot)
    {
        if (slot.ImagePath is null)
        {
            return;
        }

        try
        {
            var bmp = LoadBitmap(slot.ImagePath, slot.RotationDegrees);
            slot.PreviewImage.Source = bmp;
            slot.PreviewImage.Visibility = Visibility.Visible;

            // Resolution quality (account for rotation swapping dimensions).
            var (iw, ih) = slot.RotationDegrees % 180 == 0
                ? (bmp.PixelWidth, bmp.PixelHeight)
                : (bmp.PixelHeight, bmp.PixelWidth);
            slot.Quality = ImageAdvisor.Assess(iw, ih, slot.Monitor.Width, slot.Monitor.Height);
            UpdateBarButton(slot, iw, ih);
        }
        catch (Exception ex)
        {
            slot.Quality = ImageQuality.Poor;
            if (slot.BarButton is not null)
            {
                slot.BarButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
                slot.BarButton.ToolTip = $"Could not load image: {ex.Message}";
            }
        }
    }

    /// <summary>Select a monitor's bar button (white accent); clears the others.</summary>
    private void SelectMonitor(MonitorSlot slot)
    {
        foreach (var s in _slots.Values)
        {
            s.Selected = s == slot;
            RefreshBarButtonVisual(s);
        }
    }

    /// <summary>Rotate the currently selected monitor's image 90°.</summary>
    private void RotateSelected()
    {
        var slot = _slots.Values.FirstOrDefault(s => s.Selected && s.ImagePath is not null);
        if (slot is null)
        {
            SetStatus("Select a display with an image first, then rotate.");
            return;
        }

        slot.RotationDegrees = (slot.RotationDegrees + 90) % 360;
        RenderPreview(slot);
    }

    /// <summary>
    /// Set the bar button's warning ring from the image's resolution verdict:
    /// white (or neutral) when good, yellow when soft, red when too low. The
    /// selected button overrides the ring with the white accent.
    /// </summary>
    private void UpdateBarButton(MonitorSlot slot, int iw, int ih)
    {
        if (slot.BarButton is null)
        {
            return;
        }

        slot.BarButton.ToolTip = ImageAdvisor.Advice(slot.Quality, iw, ih, slot.Monitor.Width, slot.Monitor.Height)
            ?? $"{iw}×{ih} on a {slot.Monitor.Width}×{slot.Monitor.Height} screen — looks good.";
        RefreshBarButtonVisual(slot);
    }

    /// <summary>Apply the correct ring colour for a bar button given selection + quality.</summary>
    private void RefreshBarButtonVisual(MonitorSlot slot)
    {
        if (slot.BarButton is null)
        {
            return;
        }

        Brush ring = slot.Quality switch
        {
            ImageQuality.Poor when slot.ImagePath is not null => new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
            ImageQuality.Soft when slot.ImagePath is not null => new SolidColorBrush(Color.FromRgb(0xE0, 0xB8, 0x4D)),
            _ => Res<Brush>("CardBorder"),
        };

        // Selected monitor gets the white accent ring + brighter icon.
        if (slot.Selected)
        {
            slot.BarButton.BorderBrush = Res<Brush>("Accent");
            slot.BarButton.Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
        }
        else
        {
            slot.BarButton.BorderBrush = ring;
            slot.BarButton.Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24));
        }

        if (slot.BarButton.Child is TextBlock icon)
        {
            icon.Foreground = slot.ImagePath is not null || slot.Selected
                ? Res<Brush>("TextPrimary")
                : Res<Brush>("TextSecondary");
        }
    }

    private void SetStatus(string text)
    {
        if (_statusLabel is not null)
        {
            _statusLabel.Text = text;
            _statusLabel.Foreground = Res<Brush>("TextTertiary");
        }
    }

    private void SetStatusAccent(string text)
    {
        if (_statusLabel is not null)
        {
            _statusLabel.Text = text;
            _statusLabel.Foreground = Res<Brush>("Accent");
        }
    }

    private void SetStatusError(string text)
    {
        if (_statusLabel is not null)
        {
            _statusLabel.Text = text;
            _statusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
        }
    }

    private static BitmapImage LoadBitmap(string path, int rotation)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(path);
        bmp.Rotation = rotation switch
        {
            90 => Rotation.Rotate90,
            180 => Rotation.Rotate180,
            270 => Rotation.Rotate270,
            _ => Rotation.Rotate0,
        };
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // --- Apply (single action, all assigned monitors) ---------------------

    private void ApplyAll()
    {
        var assigned = _slots.Values.Where(s => s.ImagePath is not null).ToList();
        if (assigned.Count == 0)
        {
            SetStatus("Drag an image onto a display first.");
            return;
        }

        // In simulated mode there is no real display to set; report and stop.
        if (_simulated || _backend is null)
        {
            SetStatus($"(simulated) would apply to {assigned.Count} display(s).");
            return;
        }

        var applied = 0;
        var failures = new List<string>();
        foreach (var slot in assigned)
        {
            try
            {
                var pathToApply = slot.ImagePath!;

                // If rotated, bake a rotated copy to a temp file (COM takes a path).
                if (slot.RotationDegrees % 360 != 0)
                {
                    pathToApply = WriteRotatedCopy(slot.ImagePath!, slot.RotationDegrees);
                }

                _backend.SetMonitorWallpaper(slot.Monitor.DeviceId, pathToApply, WallpaperFit.Fill);
                applied++;
            }
            catch (Exception ex)
            {
                failures.Add($"{slot.Monitor.FriendlyName}: {ex.Message}");
            }
        }

        if (_statusLabel is not null)
        {
            if (failures.Count == 0)
            {
                _statusLabel.Text = $"Applied to {applied} display(s).";
                _statusLabel.Foreground = Res<Brush>("Accent");
            }
            else
            {
                _statusLabel.Text = $"Applied {applied}, failed {failures.Count}: {string.Join("; ", failures)}";
                _statusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            }
        }
    }

    private static string WriteRotatedCopy(string sourcePath, int rotation)
    {
        var bmp = LoadBitmap(sourcePath, rotation);
        var dir = Path.Combine(Path.GetTempPath(), "wally");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, $"rot{rotation}_{Path.GetFileNameWithoutExtension(sourcePath)}.png");
        using var fs = new FileStream(outPath, FileMode.Create);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        encoder.Save(fs);
        return outPath;
    }

    // --- Gallery strip -----------------------------------------------------

    private Border? _galleryHost;

    private Border BuildGallery()
    {
        _galleryHost = new Border
        {
            Background = Res<Brush>("NavBackground"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 4, 0, 0),
        };

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerRow = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var galleryTitle = new TextBlock
        {
            Text = "Gallery",
            Foreground = Res<Brush>("TextSecondary"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(galleryTitle, 0);
        headerRow.Children.Add(galleryTitle);
        var pick = new Button { Content = "Choose folder…", Style = Res<Style>("SubtleButton") };
        pick.Click += (_, _) => PickFolder();
        Grid.SetColumn(pick, 1);
        headerRow.Children.Add(pick);
        Grid.SetRow(headerRow, 0);
        outer.Children.Add(headerRow);

        _galleryScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        // Apply the modern slim scrollbar to this scroller's bars.
        _galleryScroller.Resources.Add(
            typeof(System.Windows.Controls.Primitives.ScrollBar),
            Res<Style>("SlimScrollBar"));

        // Translate vertical wheel / two-finger touchpad scroll into horizontal
        // movement so the strip scrolls left-right with a normal scroll gesture.
        _galleryScroller.PreviewMouseWheel += (_, e) =>
        {
            if (_galleryScroller is null)
            {
                return;
            }

            var delta = e.Delta;
            _galleryScroller.ScrollToHorizontalOffset(_galleryScroller.HorizontalOffset - delta);
            e.Handled = true;
        };
        _galleryPanel = new WrapOrStack { Orientation = Orientation.Horizontal };
        _galleryPanel.Children.Add(new TextBlock
        {
            Text = "Choose a folder to load wallpapers, then drag them onto a display above.",
            Foreground = Res<Brush>("TextTertiary"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _galleryScroller.Content = _galleryPanel;
        Grid.SetRow(_galleryScroller, 1);
        outer.Children.Add(_galleryScroller);

        _galleryHost.Child = outer;
        return _galleryHost;
    }

    private void PickFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a wallpapers folder" };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        LoadGallery(dlg.FolderName);

        // Persist the choice so it reloads on next launch.
        try
        {
            _config = _config with { GalleryFolder = dlg.FolderName };
            ConfigStore.Save(_config);
        }
        catch
        {
            // Non-fatal: the gallery still works this session.
        }
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
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
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
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 8, 0),
            BorderBrush = Res<Brush>("CardBorder"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Child = img,
            ToolTip = Path.GetFileName(path),
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        // Size the thumbnail to the gallery's current height (keeps a 16:10 look),
        // so dragging the splitter to grow the gallery enlarges the thumbnails.
        if (_galleryScroller is not null)
        {
            void Resize()
            {
                var h = Math.Max(44, _galleryScroller!.ViewportHeight - 6);
                thumb.Height = h;
                thumb.Width = h * 1.6;
            }

            _galleryScroller.SizeChanged += (_, _) => Resize();
            thumb.Loaded += (_, _) => Resize();
        }
        else
        {
            thumb.Width = 96;
            thumb.Height = 60;
        }

        // Drag source: start a drag carrying the image path.
        thumb.MouseMove += (_, e) =>
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                var data = new DataObject(DragFormat, path);
                DragDrop.DoDragDrop(thumb, data, DragDropEffects.Copy);
            }
        };

        return thumb;
    }

    // --- helpers -----------------------------------------------------------

    private T Res<T>(string key) => (T)FindResource(key);
}
