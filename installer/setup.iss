; ThinkPad Backlight Tray - per-user Inno Setup installer (no admin required)
; Called from CI with:  ISCC /DAppVersion=X.Y.Z /DPublishDir=path installer\setup.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
[Setup]
AppName=ThinkPad Backlight Tray
AppVersion={#AppVersion}
AppPublisher=loadhigh
AppPublisherURL=https://github.com/loadhigh/ThinkPad-Backlight-Tray
DefaultDirName={localappdata}\ThinkPad-Backlight-Tray
DefaultGroupName=ThinkPad Backlight Tray
UninstallDisplayIcon={app}\ThinkPad-Backlight-Tray.exe
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=ThinkPad-Backlight-Tray-Setup-v{#AppVersion}
Compression=lzma2
SolidCompression=yes
SetupIconFile=..\app.ico
DisableProgramGroupPage=yes
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
[Icons]
Name: "{group}\ThinkPad Backlight Tray"; Filename: "{app}\ThinkPad-Backlight-Tray.exe"
Name: "{group}\Uninstall ThinkPad Backlight Tray"; Filename: "{uninstallexe}"
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "ThinkPad-Backlight-Tray"; \
    ValueData: """{app}\ThinkPad-Backlight-Tray.exe"""; Flags: uninsdeletevalue
[Run]
Filename: "{app}\ThinkPad-Backlight-Tray.exe"; Description: "Launch ThinkPad Backlight Tray"; \
    Flags: nowait postinstall skipifsilent
[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM ThinkPad-Backlight-Tray.exe"; \
    Flags: runhidden; RunOnceId: "KillApp"
