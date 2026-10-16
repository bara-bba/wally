using System.Runtime.InteropServices;

namespace Wally.Platform;

/// <summary>
/// Reads the current virtual-desktop identity using the DOCUMENTED
/// <c>IVirtualDesktopManager</c> COM interface (shobjidl_core.h, Windows 10+).
///
/// This deliberately avoids the undocumented <c>IVirtualDesktopManagerInternal</c>
/// (whose GUID/vtable shifts between Windows builds). The documented interface
/// cannot enumerate every desktop or set a per-desktop wallpaper directly, but it
/// CAN report which desktop a given window is on. We query the FOREGROUND window,
/// which lives on whichever desktop the user is currently viewing — so its
/// desktop GUID is the current desktop, and polling it detects switches. The app
/// then re-applies the assigned wallpaper on a switch (the "detect and swap"
/// strategy), which is stable across Windows builds.
/// </summary>
public sealed class VirtualDesktopService : IVirtualDesktopService, IDisposable
{
    private readonly IVirtualDesktopManager _vdm;
    private IntPtr _fallbackWindow;
    private bool _disposed;

    public VirtualDesktopService()
    {
        var type = Type.GetTypeFromCLSID(new Guid(VirtualDesktopManagerClsid))
            ?? throw new InvalidOperationException("VirtualDesktopManager COM class not registered on this system.");
        _vdm = (IVirtualDesktopManager)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Failed to create IVirtualDesktopManager instance."));
    }

    /// <summary>
    /// A fallback window (typically the app's main window) used to resolve the
    /// current desktop when no suitable foreground window is available (e.g. the
    /// desktop/Task View itself is focused and returns no desktop id).
    /// </summary>
    public void TrackWindow(IntPtr hwnd) => _fallbackWindow = hwnd;

    public string CurrentDesktopGuid()
    {
        // The registry's CurrentVirtualDesktop is authoritative and tracks the
        // actual foreground desktop reliably across switches.
        var fromReg = CurrentDesktopGuidFromRegistry();
        if (!string.IsNullOrEmpty(fromReg))
        {
            return fromReg;
        }

        // Fallback: resolve via a window's desktop id (foreground, then our own).
        foreach (var hwnd in new[] { GetForegroundWindow(), _fallbackWindow })
        {
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var guid = _vdm.GetWindowDesktopId(hwnd);
                if (guid != Guid.Empty)
                {
                    return guid.ToString();
                }
            }
            catch (COMException)
            {
                // Window not on a desktop right now (mid-transition); try next.
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Enumerate all virtual desktops from the registry, which is where Windows
    /// persists the ordered desktop list. The documented COM API can only resolve
    /// a window's desktop (and reports Empty for windows on other desktops), so it
    /// cannot list desktops — the registry can, and every virtual-desktop tool
    /// relies on it. Returns GUIDs in Windows' own Desktop 1..N order.
    /// </summary>
    public IReadOnlyList<string> ListDesktopGuids()
    {
        var bytes = ReadRegistryBytes("VirtualDesktopIDs");
        if (bytes is null || bytes.Length < 16)
        {
            // Fall back to the current desktop alone if the registry is absent.
            var current = CurrentDesktopGuid();
            return string.IsNullOrEmpty(current) ? Array.Empty<string>() : new[] { current };
        }

        var guids = new List<string>();
        for (var offset = 0; offset + 16 <= bytes.Length; offset += 16)
        {
            var g = new byte[16];
            Array.Copy(bytes, offset, g, 0, 16);
            guids.Add(new Guid(g).ToString());
        }

        return guids;
    }

    /// <summary>The current desktop GUID from the registry (authoritative), or
    /// empty if unavailable.</summary>
    public string CurrentDesktopGuidFromRegistry()
    {
        var bytes = ReadRegistryBytes("CurrentVirtualDesktop");
        return bytes is { Length: 16 } ? new Guid(bytes).ToString() : string.Empty;
    }

    private static byte[]? ReadRegistryBytes(string valueName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            return key?.GetValue(valueName) as byte[];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whether the tracked fallback window is on the current (foreground) desktop.</summary>
    public bool IsTrackedWindowOnCurrentDesktop()
    {
        if (_fallbackWindow == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return _vdm.IsWindowOnCurrentVirtualDesktop(_fallbackWindow);
        }
        catch (COMException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_vdm is not null)
        {
            Marshal.FinalReleaseComObject(_vdm);
        }

        _disposed = true;
    }

    private const string VirtualDesktopManagerClsid = "AA509086-5CA9-4C25-8F95-589D3C07B48A";

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);

        Guid GetWindowDesktopId(IntPtr topLevelWindow);

        void MoveWindowToDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.LPStruct)] Guid desktopId);
    }
}
