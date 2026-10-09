"""Rule resolution: turn detected state + stored config into concrete actions.

Pure logic, no Windows calls. The platform layer consumes the ApplyAction list
and performs the actual COM wallpaper sets.
"""

from __future__ import annotations

from dataclasses import dataclass

from .models import (
    DesktopAssignment,
    Monitor,
    MonitorSetup,
    WallpaperFit,
    WallyConfig,
)


@dataclass(frozen=True)
class ApplyAction:
    """A resolved instruction: put `image_path` on monitor `device_id`."""

    device_id: str
    image_path: str
    fit: WallpaperFit


@dataclass(frozen=True)
class ResolveResult:
    actions: tuple[ApplyAction, ...]
    # Monitors in the setup that had no rule and no default - the UI should
    # prompt the user to assign these.
    unassigned: tuple[Monitor, ...]
    # True when the setup fingerprint had no SetupRule at all (new arrangement).
    is_new_setup: bool


def resolve_setup(config: WallyConfig, setup: MonitorSetup) -> ResolveResult:
    """Resolve the per-monitor wallpaper actions for the current setup.

    - Looks up the SetupRule by fingerprint.
    - For each monitor, resolves its MonitorRule (explicit or default).
    - Monitors with no resolvable image are reported as `unassigned`.
    """
    rule = config.rule_for_setup(setup.fingerprint)
    actions: list[ApplyAction] = []
    unassigned: list[Monitor] = []

    for monitor in setup.monitors:
        monitor_rule = rule.rule_for(monitor.edid_key) if rule else None
        if monitor_rule is None:
            unassigned.append(monitor)
            continue
        actions.append(
            ApplyAction(
                device_id=monitor.device_id,
                image_path=monitor_rule.image_path,
                fit=monitor_rule.fit,
            )
        )

    return ResolveResult(
        actions=tuple(actions),
        unassigned=tuple(unassigned),
        is_new_setup=rule is None,
    )


def next_rotation_image(
    config: WallyConfig, already_used: tuple[str, ...]
) -> str | None:
    """Pick the next image for auto-assign, cycling through the rotation pool
    and preferring images not yet used by current desktop assignments."""
    if not config.rotation_pool:
        return None
    used = set(already_used)
    for img in config.rotation_pool:
        if img not in used:
            return img
    # All used at least once - wrap to the first.
    return config.rotation_pool[0]


def resolve_desktop(
    config: WallyConfig, desktop_guid: str
) -> DesktopAssignment | None:
    """Return the assignment for a virtual desktop, or None if unassigned.

    When None and new_desktop_behavior == 'auto_assign', the caller should use
    next_rotation_image() to create one.
    """
    return config.assignment_for_desktop(desktop_guid)
