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
              │     ├── WMI / PowerModeChanged / display polling → OnRestoreBacklight → App.RestoreBacklight()
              │     └── Fn+Space registry watcher → OnFnSpaceLevelChanged → SettingsManager.SetBacklightLevel()
              ├── BuildTrayIcon()
              └── RestoreBacklight()   ← immediate restore on launch
```

- `App.cs` owns lifetime, tray menu actions, restore callback, Info dialog, and About dialog.
- `EventMonitor.cs` triggers restore from WMI (`Win32_SystemConfigurationChangeEvent`, `Win32_PowerSupplyEvent`),
  `SystemEvents.PowerModeChanged` (Resume), plus display-count polling fallback.
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

## Project Conventions

- Keep tray behavior in `App.cs`; keep driver IOCTL details in `PmDriverBacklightController.cs`.
- Prefer log-and-continue over throwing in provider/watcher paths.
- Registry booleans are `DWORD` `1`/`0`.
- Run at Startup toggle uses manual `CheckOnClick = false` + `RebuildTrayMenu()` to stay in sync with registry.
- In mixed WPF/WinForms files, avoid type collisions via `using` aliases where needed (`TextBox`, `Button`,
  `Color`, `FontFamily`, etc.).

## High-Value Files

- `App.cs`, `Program.cs`, `BacklightController.cs`, `PmDriverBacklightController.cs`
- `EventMonitor.cs`, `SettingsManager.cs`, `SessionHelper.cs`, `README.md`