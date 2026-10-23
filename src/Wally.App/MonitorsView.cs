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
public sealed class MonitorsView : UserControl, IShellPage
{
    public string PageTitle => "Monitors";

    public FrameworkElement? HeaderActions { get; private set; }

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
    private TextBlock? _toolbarStatus;
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

        // The wallpaper is painted as the screen border's background via an
        // ImageBrush (UniformToFill). A brush fills the rectangle deterministically
        // and clips for free — unlike an Image element, which measures to its
        // aspect ratio and can leave the screen half-filled.
        public ImageBrush PreviewBrush { get; } = new()
        {
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
        };

        public string? ImagePath { get; set; }
        public int RotationDegrees { get; set; }
        public ImageQuality Quality { get; set; } = ImageQuality.Good;

        // Normalised pan offset in [-1, 1] per axis (0 = centred). Only the axis
        // with spare image (the cropped one) moves the picture visibly.
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }

        // Pixel dimensions of the currently loaded (rotation-applied) bitmap,
        // used to compute how far the image can pan within the screen frame.
        public int BitmapWidth { get; set; }
        public int BitmapHeight { get; set; }

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

        // Study / Picky toolbar row (row 0): accent primary action, a divider,
        // then 38×34 icon buttons, with grey status text on the right.
        _toolbarStatus = PageToolbar.Status();
        var toolbar = PageToolbar.Compose(
            new UIElement[]
            {
                PageToolbar.Primary(
                    "\U0001F5AB", // 🖫 floppy
                    "Save setup",
                    "Remember the current wallpaper on each display for this exact set of monitors. "
                        + "When this set is detected again, wally re-applies it automatically.",
                    SaveCurrentSetup),
                PageToolbar.Divider(),
                PageToolbar.Tool(
                    "\u27F3", // ⟳
                    "Refresh — re-detect displays and reload the wallpaper folder",
                    RefreshAll),
                PageToolbar.Tool("\u270E", "Choose wallpaper folder…", PickFolder), // ✎
                PageToolbar.Tool(
                    "\u29C9", // ⧉
                    "Open wallpaper folder in Explorer",
                    () =>
                    {
                        if (!PageToolbar.OpenInExplorer(_config.GalleryFolder))
                        {
                            SetStatusError("Choose a wallpaper folder first.");
                        }
                    }),
            },
            _toolbarStatus);
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);
        HeaderActions = null;

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

        var layoutLabel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        layoutLabel.Children.Add(new TextBlock { Text = "DISPLAYS", Style = Res<Style>("CardLabel") });
        layoutLabel.Children.Add(new TextBlock
        {
            Text = "Drag an image from the gallery onto a display. Drag inside a display to reposition a cropped wallpaper.",
            Style = Res<Style>("CardHint"),
        });
        DockPanel.SetDock(layoutLabel, Dock.Top);
        layoutDock.Children.Add(layoutLabel);

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
                Foreground = Res<Brush>("Bad"),
                Margin = new Thickness(4),
            });
            return;
        }

        BuildMonitorSlots();
        UpdateToolbarStatus();
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
                Foreground = Res<Brush>("Bad"),
            });
            return;
        }

        foreach (var m in setup.Monitors)
        {
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

            var slotColor = m.IsPrimary
                ? Res<Brush>("AccentMuted")
                : Res<Brush>("Subtle");

            // The wallpaper is the border's Background (an ImageBrush). The brush
            // fills the rectangle (UniformToFill) and clips for free, so there is
            // never a half-filled screen. The name label sits on top.
            var border = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = slotColor, // replaced by the ImageBrush once an image is assigned
                BorderBrush = Res<Brush>("CardBorder"),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                AllowDrop = true,
                Cursor = Cursors.Hand,
                Child = label,
            };

            var slot = new MonitorSlot
            {
                Monitor = m,
                ScreenBorder = border,
            };
            _slots[m.DeviceId] = slot;

            border.Drop += (_, e) => OnDropOnMonitor(slot, e);
            border.DragEnter += (_, e) => OnDragOverMonitor(border, e);
            border.DragOver += (_, e) => OnDragOverMonitor(border, e);
            border.DragLeave += (_, _) => border.BorderBrush = Res<Brush>("CardBorder");

            // Drag inside a screen to pan its wallpaper (when it has one and is
            // cropped). Selecting happens on press so the pan acts on this slot.
            AttachPanHandlers(slot);

            _layoutCanvas!.Children.Add(border);
        }

        BuildBottomBar();
        UpdateToolbarStatus();

        // If this exact set of monitors has a saved configuration, load it into
        // the previews so the UI reflects what will be applied.
        LoadSavedSetupIntoSlots();

        // Any monitor without a saved wally assignment shows its CURRENT desktop
        // wallpaper, so the UI reflects the live system state on first open.
        SeedLiveWallpaperIntoEmptySlots();

        LayoutMonitors();
    }

    /// <summary>
    /// Re-read connected displays AND reload the gallery folder. This is what
    /// the header Refresh button calls: the old handler only re-read monitors,
    /// so images newly added to the wallpaper folder never appeared until the
    /// folder was re-picked. Reloading the gallery here picks them up.
    /// </summary>
    private void RefreshAll()
    {
        RefreshMonitors();

        if (!string.IsNullOrEmpty(_config.GalleryFolder) && Directory.Exists(_config.GalleryFolder))
        {
            LoadGallery(_config.GalleryFolder);
        }

        UpdateToolbarStatus();
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
        // keep their wallpaper/rotation/pan across the rebuild.
        var previous = _slots.Values
            .Where(s => s.ImagePath is not null)
            .ToDictionary(s => s.Monitor.DeviceId, s => (s.ImagePath!, s.RotationDegrees, s.OffsetX, s.OffsetY));

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
                slot.OffsetX = prev.Item3;
                slot.OffsetY = prev.Item4;
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
                RotationDegrees: slot.RotationDegrees,
                OffsetX: slot.OffsetX,
                OffsetY: slot.OffsetY));
        }

        try
        {
            _config = ConfigStore.Update(c => c.WithSetupRule(rule));
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
                slot.OffsetX = mr.OffsetX;
                slot.OffsetY = mr.OffsetY;
                RenderPreview(slot);
            }
        }
    }

    /// <summary>
    /// Fill any slot that has no wally assignment with the wallpaper the monitor
    /// is CURRENTLY displaying, read live from the backend. This makes the UI
    /// reflect the real desktop on first open (before anything is assigned in
    /// wally) rather than showing blank screens. No-op in simulated mode.
    /// </summary>
    private void SeedLiveWallpaperIntoEmptySlots()
    {
        if (_simulated || _backend is null)
        {
            return;
        }

        foreach (var slot in _slots.Values)
        {
            if (slot.ImagePath is not null)
            {
                continue; // a saved assignment already populated this slot
            }

            string? live;
            try { live = _backend.GetMonitorWallpaper(slot.Monitor.DeviceId); }
            catch { live = null; }

            if (live is not null && File.Exists(live))
            {
                slot.ImagePath = live;
                slot.RotationDegrees = 0;
                slot.OffsetX = 0;
                slot.OffsetY = 0;
                RenderPreview(slot);
            }
        }
    }

    /// <summary>
    /// Called on a virtual-desktop switch (and at launch). If the connected
    /// displays match a saved setup, apply it and return true — a saved setup
    /// takes priority over the per-desktop wallpaper. Re-reads the config from
    /// disk so a setup saved moments ago is honoured.
    /// </summary>
    public bool ApplySavedSetupIfAny()
    {
        try { _config = ConfigStore.Load(); }
        catch { /* keep the in-memory copy */ }

        return AutoApplySavedSetup() > 0;
    }

    /// <summary>
    /// If the current set of monitors has a saved configuration, apply those
    /// wallpapers to the real desktop now. Returns how many displays were set.
    /// No-op (0) in simulated mode or when no rule matches.
    /// </summary>
    private int AutoApplySavedSetup()
    {
        if (_simulated || _backend is null || _currentSetup is null)
        {
            return 0;
        }

        var rule = _config.RuleForSetup(_currentSetup.Fingerprint);
        if (rule is null)
        {
            return 0;
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
                var path = ProcessedPathIfNeeded(mr.ImagePath, mr.RotationDegrees, mr.OffsetX, mr.OffsetY, slot.Monitor);
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

        return applied;
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
                Background = Res<Brush>("Subtle"),
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

            // The pannable slack depends on the border size, which just changed —
            // reposition the image so the offset stays visually consistent.
            if (slot.ImagePath is not null)
            {
                slot.ScreenBorder.Dispatcher.BeginInvoke(new Action(() => ApplyPan(slot)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
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

    private void AssignImage(MonitorSlot slot, string path, int rotation = 0, double offsetX = 0, double offsetY = 0)
    {
        slot.ImagePath = path;
        slot.RotationDegrees = rotation;
        slot.OffsetX = offsetX;
        slot.OffsetY = offsetY;
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
            slot.PreviewBrush.ImageSource = bmp;
            slot.ScreenBorder.Background = slot.PreviewBrush;

            // BitmapImage.Rotation already bakes the rotation into the returned
            // bitmap, so PixelWidth/PixelHeight are the final (rotated)
            // dimensions — use them directly. Swapping again here would feed the
            // pan/aspect math the wrong orientation and stretch the image on the
            // wrong axis (e.g. a vertical photo rotated to fit a wide monitor).
            var iw = bmp.PixelWidth;
            var ih = bmp.PixelHeight;
            slot.BitmapWidth = iw;
            slot.BitmapHeight = ih;
            slot.Quality = ImageAdvisor.Assess(iw, ih, slot.Monitor.Width, slot.Monitor.Height);
            UpdateBarButton(slot, iw, ih);
            ApplyPan(slot); // position the (possibly clamped) pan offset
        }
        catch (Exception ex)
        {
            slot.Quality = ImageQuality.Poor;
            if (slot.BarButton is not null)
            {
                slot.BarButton.BorderBrush = Res<Brush>("Bad");
                slot.BarButton.ToolTip = $"Could not load image: {ex.Message}";
            }
        }
    }

    // --- Pan (move the wallpaper within a cropped screen) ------------------

    // Pan drag state, scoped to the slot currently being dragged.
    private MonitorSlot? _panSlot;
    private Point _panStart;
    private double _panStartOffsetX;
    private double _panStartOffsetY;

    /// <summary>
    /// How many device-independent pixels the image can travel from centre on
    /// each axis inside the screen frame (half the UniformToFill overflow). Zero
    /// on an axis means that axis is not cropped, so panning it does nothing.
    /// </summary>
    private static (double maxX, double maxY) PanSlack(MonitorSlot slot)
    {
        var bw = slot.ScreenBorder.ActualWidth - slot.ScreenBorder.BorderThickness.Left - slot.ScreenBorder.BorderThickness.Right;
        var bh = slot.ScreenBorder.ActualHeight - slot.ScreenBorder.BorderThickness.Top - slot.ScreenBorder.BorderThickness.Bottom;
        if (bw <= 0 || bh <= 0 || slot.BitmapWidth <= 0 || slot.BitmapHeight <= 0)
        {
            return (0, 0);
        }

        // UniformToFill: scale so the image covers the frame; overflow is slack.
        var scale = Math.Max(bw / slot.BitmapWidth, bh / slot.BitmapHeight);
        var dispW = slot.BitmapWidth * scale;
        var dispH = slot.BitmapHeight * scale;
        return (Math.Max(0, (dispW - bw) / 2), Math.Max(0, (dispH - bh) / 2));
    }

    /// <summary>Position the preview image from the slot's normalised offset,
    /// clamping to the available slack so no empty gutter is ever shown.
    /// Panning is done by moving the brush's Viewbox (the source sub-rectangle),
    /// which is the correct mechanism for an ImageBrush and leaves the
    /// UniformToFill scaling intact.</summary>
    private void ApplyPan(MonitorSlot slot)
    {
        var bw = slot.ScreenBorder.ActualWidth;
        var bh = slot.ScreenBorder.ActualHeight;
        if (bw <= 0 || bh <= 0 || slot.BitmapWidth <= 0 || slot.BitmapHeight <= 0)
        {
            // Not laid out yet — leave the brush filling (Stretch handles it).
            slot.PreviewBrush.Viewbox = new Rect(0, 0, 1, 1);
            slot.PreviewBrush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
            slot.PreviewBrush.Stretch = Stretch.UniformToFill;
            return;
        }

        var targetAspect = bw / bh;
        var imageAspect = (double)slot.BitmapWidth / slot.BitmapHeight;

        // Compute the normalised source sub-rectangle (0..1) that, when stretched
        // to fill the screen, shows the "fill" crop. Then shift it by the offset.
        double vw, vh;
        if (imageAspect > targetAspect)
        {
            // Image wider than screen: use full height, crop width.
            vh = 1.0;
            vw = targetAspect / imageAspect;
        }
        else
        {
            // Image taller/narrower: use full width, crop height.
            vw = 1.0;
            vh = imageAspect / targetAspect;
        }

        var slackX = 1.0 - vw; // >= 0
        var slackY = 1.0 - vh; // >= 0

        // Clamp the offset to axes that actually have slack.
        slot.OffsetX = slackX <= 0 ? 0 : Math.Clamp(slot.OffsetX, -1, 1);
        slot.OffsetY = slackY <= 0 ? 0 : Math.Clamp(slot.OffsetY, -1, 1);

        // offset -1 => show left/top edge, +1 => show right/bottom edge.
        var vx = (slackX / 2) + slot.OffsetX * (slackX / 2);
        var vy = (slackY / 2) + slot.OffsetY * (slackY / 2);

        // With an explicit Viewbox the brush stretches that sub-rect to fill, so
        // Stretch must be Fill (not UniformToFill) to avoid double-cropping.
        slot.PreviewBrush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
        slot.PreviewBrush.Viewbox = new Rect(vx, vy, vw, vh);
        slot.PreviewBrush.Stretch = Stretch.Fill;
        slot.PreviewBrush.RelativeTransform = null;
    }

    /// <summary>Attach press/drag/release handlers so dragging inside a screen
    /// pans its wallpaper. Pannable only when the image has slack on an axis.</summary>
    private void AttachPanHandlers(MonitorSlot slot)
    {
        var border = slot.ScreenBorder;

        border.MouseLeftButtonDown += (_, e) =>
        {
            if (slot.ImagePath is null)
            {
                return;
            }

            SelectMonitor(slot);

            var (maxX, maxY) = PanSlack(slot);
            if (maxX <= 0 && maxY <= 0)
            {
                return; // nothing to pan (image fits exactly or is letterboxed)
            }

            _panSlot = slot;
            _panStart = e.GetPosition(border);
            _panStartOffsetX = slot.OffsetX;
            _panStartOffsetY = slot.OffsetY;
            border.CaptureMouse();
            border.Cursor = Cursors.SizeAll;
            e.Handled = true;
        };

        border.MouseMove += (_, e) =>
        {
            if (_panSlot != slot || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            {
                return;
            }

            var (maxX, maxY) = PanSlack(slot);
            var now = e.GetPosition(border);
            var dx = now.X - _panStart.X;
            var dy = now.Y - _panStart.Y;

            // Grab-and-drag feel: moving the cursor right should drag the picture
            // right (revealing its left side). With the Viewbox model a positive
            // offset reveals the right side, so the delta is negated.
            if (maxX > 0)
            {
                slot.OffsetX = Math.Clamp(_panStartOffsetX - dx / maxX, -1, 1);
            }

            if (maxY > 0)
            {
                slot.OffsetY = Math.Clamp(_panStartOffsetY - dy / maxY, -1, 1);
            }

            ApplyPan(slot);
            e.Handled = true;
        };

        void EndPan()
        {
            if (_panSlot == slot)
            {
                _panSlot = null;
                border.ReleaseMouseCapture();
                border.Cursor = Cursors.Hand;
            }
        }

        border.MouseLeftButtonUp += (_, _) => EndPan();
        border.LostMouseCapture += (_, _) => EndPan();

        // Double-click recentres the wallpaper (undo a pan).
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && slot.ImagePath is not null)
            {
                slot.OffsetX = 0;
                slot.OffsetY = 0;
                ApplyPan(slot);
                SetStatus($"Recentred wallpaper on {slot.Monitor.FriendlyName}.");
                e.Handled = true;
            }
        };
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
            ImageQuality.Poor when slot.ImagePath is not null => Res<Brush>("Bad"),
            ImageQuality.Soft when slot.ImagePath is not null => Res<Brush>("Warn"),
            _ => Res<Brush>("CardBorder"),
        };

        // Selected monitor gets the white accent ring + brighter icon.
        if (slot.Selected)
        {
            slot.BarButton.BorderBrush = Res<Brush>("Accent");
            slot.BarButton.Background = Res<Brush>("SubtleHover");
        }
        else
        {
            slot.BarButton.BorderBrush = ring;
            slot.BarButton.Background = Res<Brush>("Subtle");
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
            _statusLabel.Foreground = Res<Brush>("Bad");
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
                var pathToApply = ProcessedPathIfNeeded(
                    slot.ImagePath!, slot.RotationDegrees, slot.OffsetX, slot.OffsetY, slot.Monitor);

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
                _statusLabel.Foreground = Res<Brush>("Bad");
            }
        }
    }

    /// <summary>
    /// Return a path suitable for the COM backend. If the image needs no
    /// rotation and no pan, the original path is used as-is. Otherwise a
    /// processed copy is baked to a temp PNG: rotation applied, then (if an
    /// offset is set and the image is cropped at the monitor's aspect ratio) the
    /// image is cropped to the monitor aspect at the chosen pan position, so the
    /// desktop shows exactly what the preview showed.
    /// </summary>
    private static string ProcessedPathIfNeeded(string sourcePath, int rotation, double offsetX, double offsetY, Monitor monitor)
    {
        var needsRotation = rotation % 360 != 0;
        var needsPan = offsetX != 0 || offsetY != 0;
        if (!needsRotation && !needsPan)
        {
            return sourcePath;
        }

        // Rotation-applied bitmap first.
        var bmp = LoadBitmap(sourcePath, rotation);
        BitmapSource source = bmp;

        if (needsPan && monitor.Width > 0 && monitor.Height > 0)
        {
            source = CropToAspect(bmp, monitor.Width, monitor.Height, offsetX, offsetY);
        }

        var dir = Path.Combine(Path.GetTempPath(), "wally");
        Directory.CreateDirectory(dir);
        var tag = needsPan ? $"rot{rotation}_pan_" : $"rot{rotation}_";
        var outPath = Path.Combine(dir, $"{tag}{Path.GetFileNameWithoutExtension(sourcePath)}.png");
        using var fs = new FileStream(outPath, FileMode.Create);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(fs);
        return outPath;
    }

    /// <summary>
    /// Crop <paramref name="bmp"/> to the target aspect ratio (fill style), with
    /// the crop window shifted by the normalised pan offset in [-1, 1]. This
    /// mirrors the preview's UniformToFill + translate so the baked image matches
    /// what the user positioned on screen.
    /// </summary>
    private static BitmapSource CropToAspect(BitmapSource bmp, int targetW, int targetH, double offsetX, double offsetY)
    {
        double iw = bmp.PixelWidth;
        double ih = bmp.PixelHeight;
        var targetAspect = (double)targetW / targetH;
        var imageAspect = iw / ih;

        double cropW, cropH;
        if (imageAspect > targetAspect)
        {
            // Image is wider than the screen: full height, crop the width.
            cropH = ih;
            cropW = ih * targetAspect;
        }
        else
        {
            // Image is taller/narrower: full width, crop the height.
            cropW = iw;
            cropH = iw / targetAspect;
        }

        var slackX = iw - cropW; // >= 0
        var slackY = ih - cropH; // >= 0

        // Match the preview's Viewbox mapping: offset -1 shows the left/top
        // edge, +1 shows the right/bottom edge. Crop origin moves with +offset.
        var cx = Math.Clamp((slackX / 2) + offsetX * (slackX / 2), 0, slackX);
        var cy = Math.Clamp((slackY / 2) + offsetY * (slackY / 2), 0, slackY);

        var rect = new Int32Rect(
            (int)Math.Round(cx),
            (int)Math.Round(cy),
            Math.Max(1, (int)Math.Round(cropW)),
            Math.Max(1, (int)Math.Round(cropH)));

        // Guard against rounding pushing the rect past the bitmap bounds.
        if (rect.X + rect.Width > bmp.PixelWidth)
        {
            rect.X = bmp.PixelWidth - rect.Width;
        }

        if (rect.Y + rect.Height > bmp.PixelHeight)
        {
            rect.Y = bmp.PixelHeight - rect.Height;
        }

        rect.X = Math.Max(0, rect.X);
        rect.Y = Math.Max(0, rect.Y);

        var cropped = new CroppedBitmap(bmp, rect);
        cropped.Freeze();
        return cropped;
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
            Text = "GALLERY",
            Style = Res<Style>("CardLabel"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(galleryTitle, 0);
        headerRow.Children.Add(galleryTitle);
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
            var folder = dlg.FolderName;
            _config = ConfigStore.Update(c => c with { GalleryFolder = folder });
        }
        catch
        {
            // Non-fatal: the gallery still works this session.
        }

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

    /// <summary>Right-hand toolbar text: display count, then folder path and image count.</summary>
    private void UpdateToolbarStatus()
    {
        if (_toolbarStatus is null)
        {
            return;
        }

        var displays = $"{_slots.Count} display{(_slots.Count == 1 ? "" : "s")}";
        var gallery = PageToolbar.GalleryStatus(_config.GalleryFolder, _gallery.Count);
        _toolbarStatus.Text = $"{displays}  ·  {gallery}";
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
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 8, 0),
            BorderBrush = Res<Brush>("CardBorder"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            Background = Res<Brush>("Subtle"),
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

    /// <summary>
    /// Picky/Study icon-only toolbar button: the shared ToolIconButton style
    /// (38×34, subtle surface) with a Unicode symbol glyph; the tooltip carries
    /// the label.
    /// </summary>
    private Button IconButton(string glyph, string tooltip) => new()
    {
        Style = Res<Style>("ToolIconButton"),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = tooltip,
        Content = GlyphIcon.Create(glyph),
    };

    private T Res<T>(string key) => (T)FindResource(key);
}
