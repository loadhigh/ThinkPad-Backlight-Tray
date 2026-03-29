# ThinkPad Backlight Tray - Development Instructions

## Project Overview
System-tray application that restores ThinkPad keyboard backlight after lid/power events.

## Technology Stack
- C# on .NET Framework 4.8.1 (built into Windows 10/11)
- WPF + WinForms (framework assemblies, no NuGet packages)
- WMI (`System.Management`) for event triggers
- Registry (`HKCU\Software\ThinkPad-Backlight-Tray`) for persistence

## Architecture
- `App.cs`: startup/lifecycle, tray icon/menu, Info dialog, About dialog, restore callbacks (including startup restore)
- `Program.cs`: entry point + CLI switches
- `EventMonitor.cs`: WMI watchers + polling fallback + Fn+Space registry watcher
- `BacklightController.cs`: IBMPmDrv provider wrapper + retries
- `PmDriverBacklightController.cs`: IBMPmDrv IOCTL set/get (MLCG/MLCS + KBAG/KBAS fallback)
- `SettingsManager.cs`: registry settings (`BacklightLevel`, `AutoRestore`, `RestoreLevel`, `RunAtStartup`)
- `SessionHelper.cs`: physical console session detection (skips restore in RDP)

## Provider Model
Single provider: **IBMPmDrv** kernel driver (`\\.\IBMPmDrv`).
- Primary: MLCG/MLCS — GET = CTL_CODE(34, 2464, 0, 0), SET = CTL_CODE(34, 2465, 0, 0)
- Fallback: KBAG/KBAS — GET = CTL_CODE(34, 2456, 0, 0), SET = CTL_CODE(34, 2457, 0, 0)
- SET arg (MLCS) = `level | (ThinkLight << 4) | (CycleMode << 8)`
- Keep retry behavior (3 attempts, backoff) in `BacklightController.SetBacklightLevel`.

## Build & Run
```powershell
dotnet build -c Release
.\bin\Release\net481\ThinkPad-Backlight-Tray.exe
```

## Project Conventions
- Keep IBMPmDrv IOCTL details in `PmDriverBacklightController.cs`.
- Keep tray UI behavior in `App.cs`.
- Prefer graceful degradation (log and continue) when hardware/watcher calls fail.
- Registry booleans are stored as `DWORD` `1`/`0`.
- Preserve the multi-fallback event detection model when editing `EventMonitor.cs`.
- In mixed WPF/WinForms files, avoid type collisions via `using` aliases.