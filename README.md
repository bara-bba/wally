# Wally

A Wallpaper-Engine-style wallpaper manager for Windows, focused on two things
Wallpaper Engine does poorly:

1. **Multi-monitor management** — detects connected displays, remembers rules
   per *monitor setup* (identified by the set of connected monitors), and
   reapplies the correct wallpaper to each screen automatically when the setup
   changes (dock/undock, plug/unplug).
2. **Per-virtual-desktop wallpapers** — a distinct wallpaper per Windows virtual
   desktop (Win+Ctrl+D desktops), switching automatically as you move between
   them. When a new desktop is created, Wally's manager opens so you can assign
   its wallpaper (with an auto-assign fallback).

## Status

v1 scope: **static images** (PNG/JPG/BMP). Live/video wallpapers are explicitly
deferred — the wallpaper backend is behind an interface so a video provider can
be added later without touching the rules engine.

Target: **Windows 11 24H2 (build 26100)**. The virtual-desktop COM interfaces
are undocumented and build-specific; they are isolated in
`wally/platform/virtual_desktop/` behind `VirtualDesktopService` so a different
build's GUIDs can be swapped in one place.

## Architecture

```
wally/
  core/            Pure logic, no Windows calls — unit-testable
    models.py        Monitor, MonitorSetup, DesktopAssignment, Rule dataclasses
    rules.py         Rule matching: setup-fingerprint -> per-monitor wallpaper
    store.py         JSON persistence of rules & assignments
  platform/        Windows-specific adapters (COM / Win32)
    monitors.py      Enumerate monitors via IDesktopWallpaper + EnumDisplayMonitors
    wallpaper.py     Set per-monitor wallpaper via IDesktopWallpaper (documented)
    virtual_desktop/ Undocumented VD COM, pinned to 24H2, behind an interface
  app/             UI + background service
    tray.py          System-tray background watcher
    manager.py       PySide6 manager window
  __main__.py      Entry point
tests/             pytest unit tests for core/ (no Windows needed)
```

### Design principles
- `core/` is pure Python with zero Windows imports, so the rules engine is
  fully unit-testable on any machine (CI-friendly).
- Every Windows adapter sits behind a small protocol so `core/` depends on
  interfaces, not COM.
- The undocumented virtual-desktop interface is the only build-fragile code and
  it lives in exactly one directory with a clear fallback path.

## Requirements

- Windows 11 (tested target: 24H2 / build 26100)
- Python 3.12+
- `pip install -r requirements.txt`  (pywin32, comtypes, PySide6, Pillow)

## Running

```powershell
cd wally
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python -m wally            # launches tray + manager
```

## Tests

```powershell
pip install -r requirements-dev.txt
pytest            # core/ logic only, no Windows APIs touched
```
