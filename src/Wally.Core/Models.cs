using System.Security.Cryptography;
using System.Text;

namespace Wally.Core;

/// <summary>
/// How an image is positioned on a monitor. Mirrors IDesktopWallpaper's
/// DESKTOP_WALLPAPER_POSITION enum values.
/// </summary>
public enum WallpaperFit
{
    Center,
    Tile,
    Stretch,
    Fit,
    Fill,
    Span,
}

/// <summary>
/// A single connected display.
///
/// <see cref="DeviceId"/> is the stable per-monitor identifier returned by
/// IDesktopWallpaper::GetMonitorDevicePathAt. It survives reboots and is what
/// wallpaper assignment is keyed to. <see cref="EdidKey"/> is a hardware
/// fingerprint used so a setup is recognised even if Windows reorders the
/// device paths.
/// </summary>
public sealed record Monitor(
    string DeviceId,
    string EdidKey,
    int Width,
    int Height,
    int X,
    int Y,
    bool IsPrimary = false,
    string FriendlyName = "")
{
    public string Resolution => $"{Width}x{Height}";
}

/// <summary>
/// The set of monitors currently connected, order-independent.
///
/// <see cref="Fingerprint"/> identifies the arrangement so rules can be keyed to
/// "the setup where I have the laptop panel + the 27-inch" vs "laptop alone".
/// Built from the sorted EDID keys so plug order and device-path churn do not
/// change identity.
/// </summary>
public sealed partial record MonitorSetup(IReadOnlyList<Monitor> Monitors)
{
    public string Fingerprint
    {
        get
        {
            var keys = Monitors.Select(m => m.EdidKey).OrderBy(k => k, StringComparer.Ordinal);
            var joined = string.Join("|", keys);
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
            return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
        }
    }

    public int Count => Monitors.Count;

    public Monitor? Primary()
    {
        foreach (var m in Monitors)
        {
            if (m.IsPrimary)
            {
                return m;
            }
        }

        return Monitors.Count > 0 ? Monitors[0] : null;
    }
}

/// <summary>Which wallpaper + fit to apply to one monitor within a setup.</summary>
/// <remarks>
/// <see cref="OffsetX"/>/<see cref="OffsetY"/> pan the image within the screen
/// when it is cropped to fill (aspect ratios differ). They are NORMALISED to the
/// range [-1, 1]: 0 is centred, -1/+1 push the crop fully to one edge, and the
/// value is independent of preview or screen pixel size. Only the axis with
/// spare image (the one being cropped) has any visible effect.
/// </remarks>
public sealed record MonitorRule(
    string EdidKey,
    string ImagePath,
    WallpaperFit Fit = WallpaperFit.Fill,
    int RotationDegrees = 0,
    double OffsetX = 0,
    double OffsetY = 0);

/// <summary>
/// A complete rule for one monitor setup: a wallpaper per monitor.
///
/// Keyed by <see cref="MonitorSetup.Fingerprint"/>. <see cref="PerMonitor"/> maps
/// each monitor's EdidKey to its MonitorRule. A monitor absent from the map falls
/// back to <see cref="DefaultImage"/> (if set).
/// </summary>
public sealed partial record SetupRule(
    string SetupFingerprint,
    IReadOnlyList<MonitorRule> PerMonitor,
    string? DefaultImage = null,
    WallpaperFit DefaultFit = WallpaperFit.Fill)
{
    public SetupRule(string setupFingerprint)
        : this(setupFingerprint, Array.Empty<MonitorRule>())
    {
    }

    public MonitorRule? RuleFor(string edidKey)
    {
        foreach (var r in PerMonitor)
        {
            if (r.EdidKey == edidKey)
            {
                return r;
            }
        }

        if (DefaultImage is not null)
        {
            return new MonitorRule(edidKey, DefaultImage, DefaultFit);
        }

        return null;
    }

    /// <summary>
    /// Return a new SetupRule with <paramref name="rule"/> added or replacing an
    /// existing entry for the same monitor (immutable update).
    /// </summary>
    public SetupRule WithMonitor(MonitorRule rule)
    {
        var kept = PerMonitor.Where(r => r.EdidKey != rule.EdidKey).Append(rule).ToList();
        return this with { PerMonitor = kept };
    }
}

/// <summary>
/// Wallpaper assignment for one Windows virtual desktop.
///
/// <see cref="DesktopGuid"/> is the stable GUID Windows assigns each virtual
/// desktop. When a desktop has no assignment, the app may auto-assign from a
/// rotation or open the manager (configurable).
/// </summary>
public sealed record DesktopAssignment(
    string DesktopGuid,
    string ImagePath,
    WallpaperFit Fit = WallpaperFit.Fill);

/// <summary>Top-level persisted state.</summary>
public sealed partial record WallyConfig(
    IReadOnlyList<SetupRule> SetupRules,
    IReadOnlyList<DesktopAssignment> DesktopAssignments,
    string NewDesktopBehavior = "open_manager",
    IReadOnlyList<string>? RotationPool = null)
{
    public WallyConfig()
        : this(Array.Empty<SetupRule>(), Array.Empty<DesktopAssignment>())
    {
    }

    public IReadOnlyList<string> RotationPool { get; init; } = RotationPool ?? Array.Empty<string>();

    /// <summary>Last chosen wallpapers folder for the gallery, reloaded on startup.</summary>
    public string? GalleryFolder { get; init; }

    public SetupRule? RuleForSetup(string fingerprint)
    {
        foreach (var r in SetupRules)
        {
            if (r.SetupFingerprint == fingerprint)
            {
                return r;
            }
        }

        return null;
    }

    public DesktopAssignment? AssignmentForDesktop(string guid)
    {
        foreach (var a in DesktopAssignments)
        {
            if (a.DesktopGuid == guid)
            {
                return a;
            }
        }

        return null;
    }

    public WallyConfig WithSetupRule(SetupRule rule)
    {
        var kept = SetupRules
            .Where(r => r.SetupFingerprint != rule.SetupFingerprint)
            .Append(rule)
            .ToList();
        return this with { SetupRules = kept };
    }

    public WallyConfig WithDesktopAssignment(DesktopAssignment a)
    {
        var kept = DesktopAssignments
            .Where(x => x.DesktopGuid != a.DesktopGuid)
            .Append(a)
            .ToList();
        return this with { DesktopAssignments = kept };
    }
}
