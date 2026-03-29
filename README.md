# ThinkPad Backlight Tray

System-tray application that automatically restores ThinkPad keyboard backlight
after lid-close, power, and display events.

## Stack

- C# on .NET Framework 4.8.1 (built into Windows 10/11)
- WPF for the Info and About dialog windows
- WinForms for `NotifyIcon` + `ContextMenuStrip`
- WMI (`System.Management`) for event monitoring

## Runtime behavior

- Entry point is `Program.cs` (`[STAThread]`); calls `new App().Run()`.
- `App` inherits `System.Windows.Application`, `ShutdownMode = OnExplicitShutdown`.
- On startup the saved backlight level is restored immediately.
- Event restore pipeline is `EventMonitor.cs` → `App.RestoreBacklight()` → `BacklightController.cs`.
- Backlight restore only runs on the **physical console session** (skipped in RDP).
- Backlight is controlled exclusively via the **IBMPmDrv** kernel driver (`\\.\IBMPmDrv`)
  using IOCTLs CTL_CODE(34, 2464/2465, 0, 0) with KBAG/KBAS fallback.
- Fn+Space level changes are detected via `HKLM\...\IBMPMSVC\Parameters\Notification`
  and persisted automatically.
- Tray menu and dialogs follow the OS dark / light theme.

## Install

Download the **Setup exe** from the latest
[GitHub Release](../../releases/latest) and run it — no admin required.
The installer places files under `%LOCALAPPDATA%\ThinkPad-Backlight-Tray` and
registers auto-start via `HKCU\...\Run`. Auto-start can also be toggled from
the tray menu ("Run at Startup").

A **portable zip** is also provided for manual use.

## Requirements

- Windows 10 1809+ or Windows 11 (x64)
- ThinkPad with keyboard backlight support and the Lenovo power management driver (`IBMPmDrv`)

## Build

```powershell
dotnet build -c Release
```

## Run

```powershell
.\bin\Release\net481\ThinkPad-Backlight-Tray.exe
```

## CLI

```
--off / --dim / --full     Set backlight level and exit
--restore                  Restore backlight level and exit
--restore-to <last|dim|full>
                           Set which level to restore to
--startup-on / --startup-off   Toggle run-at-startup
--info                     Print driver diagnostics and exit
--debug                    Launch tray with live debug logging to a console window
--help                     Show usage
```

## Settings

Registry root: `HKEY_CURRENT_USER\Software\ThinkPad-Backlight-Tray`

- `BacklightLevel` (DWORD): `0` Off, `1` Dim, `2` Full
- `AutoRestore` (DWORD): `1` enabled (default), `0` disabled
- `RestoreLevel` (DWORD): `0` Last (default), `1` Dim, `2` Full

## Versioning & Releases

This project uses [Semantic Versioning](https://semver.org/).

- The single source of truth for the version number is the `<Version>` property
  in `ThinkPad-Backlight-Tray.csproj`.
- All notable changes are recorded in [`CHANGELOG.md`](CHANGELOG.md) following
  the [Keep a Changelog](https://keepachangelog.com/) format.

**Release checklist:**

1. Update `<Version>` in `ThinkPad-Backlight-Tray.csproj`.
2. Add a new section to `CHANGELOG.md` with the version and date.
3. Commit: `git commit -am "Release vX.Y.Z"`.
4. Tag: `git tag vX.Y.Z`.
5. Push: `git push --follow-tags`.

Pushing a `v*` tag triggers the **Release** GitHub Action
(`.github/workflows/release.yml`), which builds the project and publishes a
portable zip and per-user installer to GitHub Releases.

## Key files

- `App.cs` — WPF Application subclass: lifecycle, NotifyIcon, tray menu, Info dialog, About dialog, restore callbacks
- `BacklightController.cs` — IBMPmDrv wrapper + retries
- `PmDriverBacklightController.cs` — IBMPmDrv IOCTL set/get (MLCG/MLCS + KBAG/KBAS fallback)
- `EventMonitor.cs` — WMI watchers + display polling + Fn+Space watcher
- `SessionHelper.cs` — physical console session detection
- `SettingsManager.cs` — registry persistence (backlight level, auto-restore, run-at-startup)
- `Program.cs` — entry point (`[STAThread]`) + CLI switch handler
- `installer/setup.iss` — Inno Setup per-user installer script

## License

MIT