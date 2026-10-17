#ifndef SourceDir
  #error SourceDir must point to the published application
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#define AppVersion "0.2.0-alpha.1"

[Setup]
AppId={{BEE45A8B-343B-42B8-A09C-EA6F2CFF1421}
AppName=Media Workbench
AppVersion={#AppVersion}
AppVerName=Media Workbench {#AppVersion}
AppPublisher=ianrastall
AppPublisherURL=https://github.com/ianrastall/media-workbench
AppSupportURL=https://github.com/ianrastall/media-workbench/issues
AppUpdatesURL=https://github.com/ianrastall/media-workbench/releases
DefaultDirName={localappdata}\Programs\Media Workbench
DefaultGroupName=Media Workbench
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir={#OutputDir}
OutputBaseFilename=MediaWorkbench-{#AppVersion}-win-x64-setup
SetupIconFile={#SourceDir}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\MediaWorkbench.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AppMutex=ianrastall.MediaWorkbench

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Retired application-owned recipe from 0.1; user data is outside {app}.
Type: files; Name: "{app}\recipes\sd15-draft.json"

[Icons]
Name: "{userprograms}\Media Workbench"; Filename: "{app}\MediaWorkbench.exe"; WorkingDir: "{app}"; AppUserModelID: "ianrastall.MediaWorkbench"
Name: "{userdesktop}\Media Workbench"; Filename: "{app}\MediaWorkbench.exe"; WorkingDir: "{app}"; AppUserModelID: "ianrastall.MediaWorkbench"; Tasks: desktopicon

[Run]
Filename: "{app}\MediaWorkbench.exe"; Description: "Open Media Workbench"; Flags: nowait postinstall skipifsilent

; User settings and media are deliberately outside {app} and are preserved on uninstall.
