using Wally.Core;

namespace Wally.Platform;

/// <summary>Enumerates the currently connected monitors as a <see cref="MonitorSetup"/>.</summary>
public interface IMonitorProvider
{
    MonitorSetup CurrentSetup();
}

/// <summary>Sets the wallpaper on a single monitor by its device path.</summary>
public interface IWallpaperBackend
{
    void SetMonitorWallpaper(string deviceId, string imagePath, WallpaperFit fit);

    /// <summary>The wallpaper currently applied to a monitor, or null if none.</summary>
    string? GetMonitorWallpaper(string deviceId);
}

/// <summary>Reads virtual-desktop state. Backed by build-pinned COM on Windows.</summary>
public interface IVirtualDesktopService
{
    /// <summary>GUID of the virtual desktop currently in the foreground.</summary>
    string CurrentDesktopGuid();

    /// <summary>All virtual desktop GUIDs, in order.</summary>
    IReadOnlyList<string> ListDesktopGuids();
}
