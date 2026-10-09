"""Protocols the app depends on, so UI/core never import COM directly.

A test or non-Windows environment can supply fakes implementing these.
"""

from __future__ import annotations

from typing import Protocol

from ..core.models import MonitorSetup, WallpaperFit


class MonitorProvider(Protocol):
    def current_setup(self) -> MonitorSetup:
        """Enumerate the currently connected monitors as a MonitorSetup."""
        ...


class WallpaperBackend(Protocol):
    def set_monitor_wallpaper(
        self, device_id: str, image_path: str, fit: WallpaperFit
    ) -> None:
        """Set the wallpaper on a single monitor by its device path."""
        ...


class VirtualDesktopService(Protocol):
    def current_desktop_guid(self) -> str:
        """GUID of the virtual desktop currently in the foreground."""
        ...

    def list_desktop_guids(self) -> tuple[str, ...]:
        """All virtual desktop GUIDs, in order."""
        ...
