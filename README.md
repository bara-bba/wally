# Wally

A Wallpaper-Engine-style wallpaper manager for Windows 11, focused on two things
Wallpaper Engine handles poorly or not at all:

1. **Multi-monitor management** — detects connected displays and remembers
   per-setup rules, so the right wallpapers reapply automatically when you
   dock/undock or plug in a monitor.
2. **Per-virtual-desktop wallpapers** — a different wallpaper on each Windows
   virtual desktop (Win+Tab → New Desktop), switching automatically as you move
   between them.

v1 scope is **static images**. Live/video wallpaper is a later layer.

## Stack

- **C# / .NET 8**, WPF for the manager UI + system tray.
- Target: **Windows 11, build 26100 (24H2)**. The virtual-desktop COM interfaces
  are undocumented and build-specific, so they are pinned to this build family
  and isolated behind an adapter.

## Project layout

```
Wally.sln
├── src/
│   ├── Wally.Core/        Pure domain logic — no Windows calls, fully testable
│   │   ├── Models.cs          Immutable records: Monitor, MonitorSetup, rules, config
│   │   ├── RulesEngine.cs     Resolve detected state + config → concrete apply actions
│   │   ├── ConfigStore.cs     Atomic JSON persistence (temp file + replace)
│   │   └── Equality.cs        Structural equality for collection-bearing records
│   ├── Wally.Platform/    Windows integration (COM), referenced only by the app
│   │   ├── Interfaces.cs                IMonitorProvider / IWallpaperBackend / IVirtualDesktopService
│   │   └── DesktopWallpaperBackend.cs   IDesktopWallpaper COM — per-monitor wallpaper (documented, stable)
│   └── Wally.App/         WPF manager window + system tray (in progress)
└── tests/
    └── Wally.Core.Tests/  xUnit — 15 tests over the pure core
```

The risky, undocumented **virtual-desktop** layer lives behind
`IVirtualDesktopService` so per-build COM differences never leak into the core or
the UI.

## Status

**Done & verified**
- Core domain, rules engine, JSON store — **15 unit tests passing**.
- `DesktopWallpaperBackend` (multi-monitor `IDesktopWallpaper`) — **live-tested**:
  enumerates the real display, derives a stable per-monitor EDID key and setup
  fingerprint.

**Unbuilt (critical path)**
- Virtual-desktop COM service (pinned to build 26100).
- Background watcher — reacts to monitor plug/unplug and desktop switches.
- WPF manager UI — assign images per monitor/per setup.
- System tray + autostart.

## Prerequisites

The .NET 8 SDK. If `dotnet` is not on your PATH it may still be installed under
`%USERPROFILE%\.dotnet` — invoke it from there, or add it to PATH.

## Build & test

```powershell
dotnet build Wally.sln
dotnet test  Wally.sln
```

## Run (once the app layer lands)

```powershell
dotnet run --project src/Wally.App
```
