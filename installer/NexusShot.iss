; Inno Setup script for NexusShot.
; Build via `.\build.ps1 installer`, which publishes the app and passes these defines; the
; fallbacks below only exist so the script also compiles directly from the Inno Setup IDE.
#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\dist"
#endif

#define AppName "NexusShot"
; A single Native AOT executable: no runtime, no framework payload, nothing else to ship.
#define AppExeName "NexusShot.exe"
#define AppPublisher "NexusAI"

[Setup]
; Never change this AppId: it is how Windows recognises upgrades of an existing install.
AppId={{9B1C6E8A-4D2F-4A47-9C67-3E5A1F0D8B21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no UAC prompt); the dialog still allows machine-wide.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=NexusShot-{#AppVersion}
SetupIconFile=..\assets\icons\nexus-shot.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The app runs in the tray; Restart Manager closes it cleanly before overwriting files.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The one file. Named explicitly rather than globbed, because the installer is itself written into
; the publish directory and a wildcard would package the previous build inside the new one.
Source: "{#PublishDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The tray process holds no window, so Restart Manager alone may not see it at uninstall time.
Filename: "{cmd}"; Parameters: "/C taskkill /IM ""{#AppExeName}"" /F"; Flags: runhidden; RunOnceId: "KillTray"
; Remove the HKCU Run entry the in-app "Start with Windows" toggle may have written.
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v NexusShot /f"; Flags: runhidden; RunOnceId: "RemoveRunKey"
