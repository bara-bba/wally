"""Multi-monitor wallpaper backend via the documented IDesktopWallpaper COM API.

IDesktopWallpaper (shobjidl_core.h, Windows 8+) is fully documented and stable,
unlike the virtual-desktop interfaces. It gives per-monitor wallpaper control
keyed by a stable monitor device path.

Monitor hardware identity (edid_key) is derived from the device path tail, which
encodes the monitor's hardware id + instance. This is stable enough to key
per-setup rules; if two identical models are swapped between ports the paths
still differ by instance, which is the behaviour we want.
"""

from __future__ import annotations

import comtypes
import comtypes.client
from ctypes import POINTER, c_int, c_uint, c_wchar_p
from ctypes.wintypes import RECT

from ..core.models import Monitor, MonitorSetup, WallpaperFit

# CLSID / IID for DesktopWallpaper (from shobjidl_core.h).
_CLSID_DesktopWallpaper = "{C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD}"
_IID_IDesktopWallpaper = "{B92B56A9-8B55-4E14-9A89-0199BBB6F93B}"

# DESKTOP_WALLPAPER_POSITION enum -> our WallpaperFit.
_FIT_TO_POS = {
    WallpaperFit.CENTER: 0,
    WallpaperFit.TILE: 1,
    WallpaperFit.STRETCH: 2,
    WallpaperFit.FIT: 3,
    WallpaperFit.FILL: 4,
    WallpaperFit.SPAN: 5,
}


class _IDesktopWallpaper(comtypes.IUnknown):
    _iid_ = comtypes.GUID(_IID_IDesktopWallpaper)
    _methods_ = [
        comtypes.COMMETHOD([], comtypes.HRESULT, "SetWallpaper",
                           (["in"], c_wchar_p, "monitorID"),
                           (["in"], c_wchar_p, "wallpaper")),
        comtypes.COMMETHOD([], comtypes.HRESULT, "GetWallpaper",
                           (["in"], c_wchar_p, "monitorID"),
                           (["out"], POINTER(c_wchar_p), "wallpaper")),
        comtypes.COMMETHOD([], comtypes.HRESULT, "GetMonitorDevicePathAt",
                           (["in"], c_uint, "monitorIndex"),
                           (["out"], POINTER(c_wchar_p), "monitorID")),
        comtypes.COMMETHOD([], comtypes.HRESULT, "GetMonitorDevicePathCount",
                           (["out"], POINTER(c_uint), "count")),
        comtypes.COMMETHOD([], comtypes.HRESULT, "GetMonitorRECT",
                           (["in"], c_wchar_p, "monitorID"),
                           (["out"], POINTER(RECT), "displayRect")),
        comtypes.COMMETHOD([], comtypes.HRESULT, "SetBackgroundColor",
                           (["in"], c_uint, "color")),
        comtypes.COMMETHOD([], comtypes.HRESULT, "GetBackgroundColor",
                           (["out"], POINTER(c_uint), "color")),
        comtypes.COMMETHOD([], comtypes.HRESULT, "SetPosition",
                           (["in"], c_int, "position")),
        comtypes.COMMETHOD([], comtypes.HRESULT, "GetPosition",
                           (["out"], POINTER(c_int), "position")),
    ]


def _edid_key_from_path(device_path: str) -> str:
    r"""Derive a stable hardware-ish key from the monitor device path.

    Paths look like:
      \\?\DISPLAY#DELA1CF#5&...&UID4352#{guid}
    We keep the hardware id segment (DELA1CF) + the UID so identical models on
    different ports stay distinct.
    """
    parts = device_path.strip("\\?").split("#")
    if len(parts) >= 3:
        return f"{parts[1]}_{parts[2].split('&')[-1]}"
    return device_path


class DesktopWallpaperBackend:
    """Implements both MonitorProvider and WallpaperBackend protocols."""

    def __init__(self) -> None:
        comtypes.CoInitialize()
        self._dw = comtypes.client.CreateObject(
            comtypes.GUID(_CLSID_DesktopWallpaper),
            interface=_IDesktopWallpaper,
        )

    def current_setup(self) -> MonitorSetup:
        count = self._dw.GetMonitorDevicePathCount()
        monitors: list[Monitor] = []
        for i in range(count):
            device_id = self._dw.GetMonitorDevicePathAt(i)
            if not device_id:
                continue
            try:
                rect = self._dw.GetMonitorRECT(device_id)
            except comtypes.COMError:
                # A path can exist for a detached monitor; skip it.
                continue
            monitors.append(
                Monitor(
                    device_id=device_id,
                    edid_key=_edid_key_from_path(device_id),
                    width=rect.right - rect.left,
                    height=rect.bottom - rect.top,
                    x=rect.left,
                    y=rect.top,
                    is_primary=(rect.left == 0 and rect.top == 0),
                    friendly_name=_edid_key_from_path(device_id),
                )
            )
        return MonitorSetup(monitors=tuple(monitors))

    def set_monitor_wallpaper(
        self, device_id: str, image_path: str, fit: WallpaperFit
    ) -> None:
        # Position is a global property in this API; set it before applying.
        self._dw.SetPosition(_FIT_TO_POS[fit])
        self._dw.SetWallpaper(device_id, image_path)
