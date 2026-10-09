"""Pure domain models for Wally. No Windows imports - fully unit-testable.

All models are immutable (frozen dataclasses) per the project coding style:
operations return new copies rather than mutating in place.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass, field, replace
from enum import Enum
from typing import Mapping


class WallpaperFit(str, Enum):
    """How an image is positioned on a monitor. Mirrors IDesktopWallpaper's
    DESKTOP_WALLPAPER_POSITION enum values."""

    CENTER = "center"
    TILE = "tile"
    STRETCH = "stretch"
    FIT = "fit"
    FILL = "fill"
    SPAN = "span"


@dataclass(frozen=True)
class Monitor:
    """A single connected display.

    `device_id` is the stable per-monitor identifier returned by
    IDesktopWallpaper::GetMonitorDevicePathAt. It survives reboots and is what
    we key wallpaper assignment to. `edid_key` is a hardware fingerprint
    (manufacturer+serial) used so a setup is recognised even if Windows
    reorders the device paths.
    """

    device_id: str
    edid_key: str
    width: int
    height: int
    x: int
    y: int
    is_primary: bool = False
    friendly_name: str = ""

    @property
    def resolution(self) -> str:
        return f"{self.width}x{self.height}"


@dataclass(frozen=True)
class MonitorSetup:
    """The set of monitors currently connected, order-independent.

    `fingerprint` identifies the *arrangement* so rules can be keyed to
    "the setup where I have the laptop panel + the 27-inch" vs "laptop alone".
    Built from the sorted EDID keys so plug order and device-path churn do not
    change identity.
    """

    monitors: tuple[Monitor, ...]

    @property
    def fingerprint(self) -> str:
        keys = sorted(m.edid_key for m in self.monitors)
        digest = hashlib.sha256("|".join(keys).encode("utf-8")).hexdigest()
        return digest[:16]

    @property
    def count(self) -> int:
        return len(self.monitors)

    def primary(self) -> Monitor | None:
        for m in self.monitors:
            if m.is_primary:
                return m
        return self.monitors[0] if self.monitors else None


@dataclass(frozen=True)
class MonitorRule:
    """Which wallpaper + fit to apply to one monitor within a setup."""

    edid_key: str
    image_path: str
    fit: WallpaperFit = WallpaperFit.FILL


@dataclass(frozen=True)
class SetupRule:
    """A complete rule for one monitor setup: a wallpaper per monitor.

    Keyed by MonitorSetup.fingerprint. `per_monitor` maps each monitor's
    edid_key to its MonitorRule. A monitor absent from the map falls back to
    `default_image` (if set).
    """

    setup_fingerprint: str
    per_monitor: tuple[MonitorRule, ...] = ()
    default_image: str | None = None
    default_fit: WallpaperFit = WallpaperFit.FILL

    def rule_for(self, edid_key: str) -> MonitorRule | None:
        for r in self.per_monitor:
            if r.edid_key == edid_key:
                return r
        if self.default_image is not None:
            return MonitorRule(edid_key, self.default_image, self.default_fit)
        return None

    def with_monitor(self, rule: MonitorRule) -> "SetupRule":
        """Return a new SetupRule with `rule` added or replacing an existing
        entry for the same monitor (immutable update)."""
        kept = tuple(r for r in self.per_monitor if r.edid_key != rule.edid_key)
        return replace(self, per_monitor=kept + (rule,))


@dataclass(frozen=True)
class DesktopAssignment:
    """Wallpaper assignment for one Windows virtual desktop.

    `desktop_guid` is the stable GUID Windows assigns each virtual desktop.
    When a desktop has no assignment, the app may auto-assign from a rotation
    or open the manager (configurable).
    """

    desktop_guid: str
    image_path: str
    fit: WallpaperFit = WallpaperFit.FILL


@dataclass(frozen=True)
class WallyConfig:
    """Top-level persisted state."""

    setup_rules: tuple[SetupRule, ...] = ()
    desktop_assignments: tuple[DesktopAssignment, ...] = ()
    # When a brand-new virtual desktop appears: "open_manager" or "auto_assign".
    new_desktop_behavior: str = "open_manager"
    # Pool of images used for auto-assign rotation.
    rotation_pool: tuple[str, ...] = ()

    def rule_for_setup(self, fingerprint: str) -> SetupRule | None:
        for r in self.setup_rules:
            if r.setup_fingerprint == fingerprint:
                return r
        return None

    def assignment_for_desktop(self, guid: str) -> DesktopAssignment | None:
        for a in self.desktop_assignments:
            if a.desktop_guid == guid:
                return a
        return None

    def with_setup_rule(self, rule: SetupRule) -> "WallyConfig":
        kept = tuple(
            r for r in self.setup_rules
            if r.setup_fingerprint != rule.setup_fingerprint
        )
        return replace(self, setup_rules=kept + (rule,))

    def with_desktop_assignment(self, a: DesktopAssignment) -> "WallyConfig":
        kept = tuple(
            x for x in self.desktop_assignments if x.desktop_guid != a.desktop_guid
        )
        return replace(self, desktop_assignments=kept + (a,))
