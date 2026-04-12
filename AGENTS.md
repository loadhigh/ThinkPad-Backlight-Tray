# ThinkPad Backlight Tray — Agent Guide

## Current Snapshot

- **Framework**: .NET Framework 4.8.1 (built into Windows 10/11).
- WPF is used for the Info and About dialog windows.
- WinForms is used for tray icon/menu (`NotifyIcon`, `ContextMenuStrip`).
- App entry point: `Program.cs` `[STAThread] Main` → `new App().Run()`.
- Backlight control is via the IBMPmDrv kernel driver only (`\\.\IBMPmDrv`).
- Restore is gated by `SessionHelper.IsConsoleSession()` (skip in RDP sessions).

## Architecture and Data Flow

```
Program.Main() [STAThread]
  ├── CLI args? → TryHandleCommand() → exit
  └── new App().Run()
        └── App.OnStartup()
              ├── SettingsManager.Initialize()
              ├── BacklightController.Initialize()
              ├── EventMonitor.Start()
              │     ├── WMI / display polling → OnRestoreBacklight → App.RestoreBacklight()
              │     ├── PowerModeChanged (Resume) → hold-off timer → OnResumeRestoreBacklight → App.KickAndRestoreBacklight()
              │     └── Fn+Space registry watcher → OnFnSpaceLevelChanged → SettingsManager.SetBacklightLevel()
              ├── BuildTrayIcon()
              └── RestoreBacklight()   ← immediate restore on launch
```

- `App.cs` owns lifetime, tray menu actions, restore callback, Info dialog, and About dialog.
- `EventMonitor.cs` triggers normal restore from WMI (`Win32_SystemConfigurationChangeEvent`, `Win32_PowerSupplyEvent`)
  plus display-count polling fallback, and triggers a dedicated resume restore path from
  `SystemEvents.PowerModeChanged` (Resume).
- `EventMonitor.cs` uses WMI debounce (`WmiDebouncePeriodMs`), resume hold-off (`ResumeHoldoffMs`), and Fn+Space
  suppression (`FnSpaceSuppressMs`) windows to avoid noisy/desynced restores.
- `BacklightController.cs` is static and wraps a single provider with retry logic.
- `SettingsManager.cs` persists `BacklightLevel`, `AutoRestore`, `RestoreLevel`, and run-at-startup settings under
  `HKCU\Software\ThinkPad-Backlight-Tray`.

## Provider and IOCTL Contract

- Provider implementation is `PmDriverBacklightController.cs`.
- Primary path: MLCG/MLCS — GET = `CTL_CODE(34, 2464, 0, 0)`, SET = `CTL_CODE(34, 2465, 0, 0)`.
- Fallback path: KBAG/KBAS — GET = `CTL_CODE(34, 2456, 0, 0)`, SET = `CTL_CODE(34, 2457, 0, 0)`.
- SET argument (MLCS) = `level | (ThinkLight << 4) | (CycleMode << 8)`
- Keep retries in `BacklightController.SetBacklightLevel` at 3 attempts with backoff.

## Fn+Space Sync

- `EventMonitor` watches:
  `HKLM\SYSTEM\CurrentControlSet\Services\IBMPMSVC\Parameters\Notification`
- `RegNotifyChangeKeyValue` bit-17 flip indicates Fn+Space level change.
- On detection, persist the new level via `SettingsManager.SetBacklightLevel()`.

## Build and Run

```powershell
dotnet build -c Release
.\bin\Release\net481\ThinkPad-Backlight-Tray.exe
```

```powershell
dotnet tool install --global wix
dotnet build installer\ThinkPad-Backlight-Tray.Installer.wixproj -p:Version=1.0.3 -p:PublishDir=$(Resolve-Path .\publish)
```

## Project Conventions

- Keep tray behavior in `App.cs`; keep driver IOCTL details in `PmDriverBacklightController.cs`.
- Prefer log-and-continue over throwing in provider/watcher paths, except unrecoverable full startup failure in
  `EventMonitor.Start()` which throws `InvalidOperationException` after cleanup.
- Registry booleans are `DWORD` `1`/`0`.
- Run at Startup toggle uses manual `CheckOnClick = false` + `RebuildTrayMenu()` to stay in sync with registry.
- `SettingsManager` uses lazy self-healing initialization via `EnsureInitialized()`; keep that behavior when adding
  new settings accessors.
- In mixed WPF/WinForms files, avoid type collisions via `using` aliases where needed (`TextBox`, `Button`,
  `Color`, `FontFamily`, etc.).

## High-Value Files

- `App.cs`, `Program.cs`, `BacklightController.cs`, `PmDriverBacklightController.cs`
- `EventMonitor.cs`, `SettingsManager.cs`, `SessionHelper.cs`, `README.md`
- `installer/setup.wxs`, `installer/ThinkPad-Backlight-Tray.Installer.wixproj`, `.github/workflows/release.yml`