"""JSON persistence for WallyConfig.

Serialises the immutable dataclass tree to/from a single JSON file. Writes are
atomic (temp file + replace) so a crash mid-write cannot corrupt the config.
Load validates structure and fails fast with a clear message on bad data.
"""

from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path

from .models import (
    DesktopAssignment,
    MonitorRule,
    SetupRule,
    WallpaperFit,
    WallyConfig,
)

_SCHEMA_VERSION = 1


def default_config_path() -> Path:
    base = os.environ.get("APPDATA") or str(Path.home())
    return Path(base) / "Wally" / "config.json"


def _fit(value: str) -> WallpaperFit:
    try:
        return WallpaperFit(value)
    except ValueError as exc:
        raise ValueError(f"Unknown wallpaper fit: {value!r}") from exc


def to_dict(config: WallyConfig) -> dict:
    return {
        "schema": _SCHEMA_VERSION,
        "new_desktop_behavior": config.new_desktop_behavior,
        "rotation_pool": list(config.rotation_pool),
        "setup_rules": [
            {
                "setup_fingerprint": r.setup_fingerprint,
                "default_image": r.default_image,
                "default_fit": r.default_fit.value,
                "per_monitor": [
                    {
                        "edid_key": m.edid_key,
                        "image_path": m.image_path,
                        "fit": m.fit.value,
                    }
                    for m in r.per_monitor
                ],
            }
            for r in config.setup_rules
        ],
        "desktop_assignments": [
            {
                "desktop_guid": a.desktop_guid,
                "image_path": a.image_path,
                "fit": a.fit.value,
            }
            for a in config.desktop_assignments
        ],
    }


def from_dict(data: dict) -> WallyConfig:
    if not isinstance(data, dict):
        raise ValueError("Config root must be a JSON object")
    schema = data.get("schema")
    if schema != _SCHEMA_VERSION:
        raise ValueError(
            f"Unsupported config schema {schema!r}; expected {_SCHEMA_VERSION}"
        )

    setup_rules = tuple(
        SetupRule(
            setup_fingerprint=r["setup_fingerprint"],
            default_image=r.get("default_image"),
            default_fit=_fit(r.get("default_fit", "fill")),
            per_monitor=tuple(
                MonitorRule(
                    edid_key=m["edid_key"],
                    image_path=m["image_path"],
                    fit=_fit(m.get("fit", "fill")),
                )
                for m in r.get("per_monitor", [])
            ),
        )
        for r in data.get("setup_rules", [])
    )

    desktop_assignments = tuple(
        DesktopAssignment(
            desktop_guid=a["desktop_guid"],
            image_path=a["image_path"],
            fit=_fit(a.get("fit", "fill")),
        )
        for a in data.get("desktop_assignments", [])
    )

    return WallyConfig(
        setup_rules=setup_rules,
        desktop_assignments=desktop_assignments,
        new_desktop_behavior=data.get("new_desktop_behavior", "open_manager"),
        rotation_pool=tuple(data.get("rotation_pool", [])),
    )


def load(path: Path | None = None) -> WallyConfig:
    path = path or default_config_path()
    if not path.exists():
        return WallyConfig()
    try:
        raw = path.read_text(encoding="utf-8")
        return from_dict(json.loads(raw))
    except (json.JSONDecodeError, KeyError, ValueError) as exc:
        raise ValueError(f"Corrupt or invalid Wally config at {path}: {exc}") from exc


def save(config: WallyConfig, path: Path | None = None) -> Path:
    path = path or default_config_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(to_dict(config), indent=2)

    # Atomic write: temp file in same dir, then os.replace.
    fd, tmp = tempfile.mkstemp(dir=str(path.parent), suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as fh:
            fh.write(payload)
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)
    return path
