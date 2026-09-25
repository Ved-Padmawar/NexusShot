; Inno Setup script for NexusShot.
; Build via `.\build.ps1 installer`, which publishes the app and passes these defines.
;
; The version is required rather than defaulted: a fallback here is a second declaration of it, and
; a stale one silently ships an installer whose name and version disagree with the exe inside it.
; build.ps1 reads it from the csproj, which is the one place it is declared.
#ifndef AppVersion
  #error AppVersion is not set - build with `.\build.ps1 installer`, or pass /DAppVersion=x.y.z
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
; Installed apps shows the bare name; the version stays in its own column via AppVersion.
UninstallDisplayName={#AppName}
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
; Named explicitly rather than globbed, because the installer is itself written into the publish
; directory and a wildcard would package the previous build inside the new one.
Source: "{#PublishDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; "Open with" for the image types the editor reads (Core/ImageFiles.cs). An Applications entry
; offers NexusShot without taking over any type's default app.
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".png"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".jpg"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".jpeg"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".bmp"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.png\OpenWithList\{#AppExeName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.jpg\OpenWithList\{#AppExeName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.jpeg\OpenWithList\{#AppExeName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.bmp\OpenWithList\{#AppExeName}"; Flags: uninsdeletekey

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; The in-app updater runs this installer silently with /UPDATE=1 and exits; start the new version.
Filename: "{app}\{#AppExeName}"; Flags: nowait; Check: IsUpdate

[UninstallRun]
; The tray process holds no window, so Restart Manager alone may not see it at uninstall time.
Filename: "{cmd}"; Parameters: "/C taskkill /IM ""{#AppExeName}"" /F"; Flags: runhidden; RunOnceId: "KillTray"
; Remove the HKCU Run entry the in-app "Start with Windows" toggle may have written.
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v NexusShot /f"; Flags: runhidden; RunOnceId: "RemoveRunKey"

[Code]
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:update|0}') = '1';
end;
