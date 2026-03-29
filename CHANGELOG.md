# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and
this project adheres to [Semantic Versioning](https://semver.org/).

## [1.0.0] – 2026-03-29

### Added

- System-tray application (WPF + WinForms) that automatically restores
  ThinkPad keyboard backlight after lid-close, power, and display events
- Backlight control via the Lenovo IBMPmDrv kernel driver (`\\.\IBMPmDrv`)
  with MLCG/MLCS IOCTLs and KBAG/KBAS fallback
- Tray context menu: Restore Now, Auto Restore toggle, Run at Startup toggle, Info…, About…, Exit
- Double-click tray icon to restore backlight
- DPI-aware tray menu (`EnableVisualStyles`, `SystemFonts.MenuFont`,
  `App.config` PerMonitorV2 opt-in)
- Dark / light theme-aware tray menu, Info dialog, and About dialog
- Backlight restore on application startup
- WMI event watchers (`Win32_SystemConfigurationChangeEvent`,
  `Win32_PowerSupplyEvent`) plus display-count polling fallback
- Fn+Space detection via `HKLM\...\IBMPMSVC\Parameters\Notification`
  registry watcher (bit-17 flip); new level persisted automatically
- Registry persistence under `HKCU\Software\ThinkPad-Backlight-Tray`
- Physical console session guard — backlight restore is skipped in RDP /
  remote desktop sessions
- CLI switches: `--off`, `--dim`, `--full`, `--restore`, `--startup-on`,
  `--startup-off`, `--info`, `--debug`, `--help`
- Auto-start via `HKCU\...\Run` registry key (toggleable from tray menu)
- Per-user Inno Setup installer (no admin required), installs to
  `%LOCALAPPDATA%\ThinkPad-Backlight-Tray`
- `SystemEvents.PowerModeChanged` (Resume) handler — primary trigger for
  lid-open / wake-from-sleep restore on ThinkPads; 1 s delay lets hardware
  finish waking before the IOCTL is sent
- Fn+Space level-change events suppressed for 5 s after any restore trigger
  as a safety margin against spurious hardware-reset notifications
