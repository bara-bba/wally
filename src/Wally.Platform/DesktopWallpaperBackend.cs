using System.Runtime.InteropServices;
using Wally.Core;
using Monitor = Wally.Core.Monitor;

namespace Wally.Platform;

/// <summary>
/// Multi-monitor wallpaper backend via the documented IDesktopWallpaper COM API.
///
/// IDesktopWallpaper (shobjidl_core.h, Windows 8+) is fully documented and stable,
/// unlike the virtual-desktop interfaces. It gives per-monitor wallpaper control
/// keyed by a stable monitor device path.
///
/// Monitor hardware identity (EdidKey) is derived from the device path tail, which
/// encodes the monitor's hardware id + instance. This is stable enough to key
/// per-setup rules; if two identical models are swapped between ports the paths
/// still differ by instance, which is the behaviour we want.
/// </summary>
public sealed class DesktopWallpaperBackend : IMonitorProvider, IWallpaperBackend, IDisposable
{
    private readonly IDesktopWallpaper _dw;
    private bool _disposed;

    public DesktopWallpaperBackend()
    {
        var type = Type.GetTypeFromCLSID(new Guid(DesktopWallpaperClsid))
            ?? throw new InvalidOperationException("DesktopWallpaper COM class not registered on this system.");
        _dw = (IDesktopWallpaper)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Failed to create IDesktopWallpaper instance."));
    }

    public MonitorSetup CurrentSetup()
    {
        var count = _dw.GetMonitorDevicePathCount();
        var monitors = new List<Monitor>();

        for (uint i = 0; i < count; i++)
        {
            var deviceId = _dw.GetMonitorDevicePathAt(i);
            if (string.IsNullOrEmpty(deviceId))
            {
                continue;
            }

            RECT rect;
            try
            {
                rect = _dw.GetMonitorRECT(deviceId);
            }
            catch (COMException)
            {
                // A path can exist for a detached monitor; skip it.
                continue;
            }

            var key = EdidKeyFromPath(deviceId);
            monitors.Add(new Monitor(
                DeviceId: deviceId,
                EdidKey: key,
                Width: rect.Right - rect.Left,
                Height: rect.Bottom - rect.Top,
                X: rect.Left,
                Y: rect.Top,
                IsPrimary: rect.Left == 0 && rect.Top == 0,
                FriendlyName: key));
        }

        return new MonitorSetup(monitors);
    }

    public void SetMonitorWallpaper(string deviceId, string imagePath, WallpaperFit fit)
    {
        // Position is a global property in this API; set it before applying.
        _dw.SetPosition(FitToPosition(fit));
        _dw.SetWallpaper(deviceId, imagePath);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_dw is not null)
        {
            Marshal.FinalReleaseComObject(_dw);
        }

        _disposed = true;
    }

    /// <summary>
    /// Derive a stable hardware-ish key from the monitor device path.
    /// Paths look like: \\?\DISPLAY#DELA1CF#5&amp;...&amp;UID4352#{guid}
    /// We keep the hardware id segment (DELA1CF) + the UID so identical models on
    /// different ports stay distinct.
    /// </summary>
    internal static string EdidKeyFromPath(string devicePath)
    {
        var parts = devicePath.Trim('\\', '?').Split('#');
        if (parts.Length >= 3)
        {
            var uidSegment = parts[2].Split('&')[^1];
            return $"{parts[1]}_{uidSegment}";
        }

        return devicePath;
    }

    // DESKTOP_WALLPAPER_POSITION enum value for a given fit.
    private static int FitToPosition(WallpaperFit fit) => fit switch
    {
        WallpaperFit.Center => 0,
        WallpaperFit.Tile => 1,
        WallpaperFit.Stretch => 2,
        WallpaperFit.Fit => 3,
        WallpaperFit.Fill => 4,
        WallpaperFit.Span => 5,
        _ => 4,
    };

    private const string DesktopWallpaperClsid = "C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD";

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID,
                          [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);

        uint GetMonitorDevicePathCount();

        RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID);

        void SetBackgroundColor(uint color);

        uint GetBackgroundColor();

        void SetPosition(int position);

        int GetPosition();
    }
}
