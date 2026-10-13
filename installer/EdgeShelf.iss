#ifndef MyAppVersion
  #define MyAppVersion "0.1.1"
#endif

#define MyAppName "Edge Shelf"
#define MyAppExeName "EdgeShelf.exe"

[Setup]
AppId={{50DB47C8-7F9D-47A1-A460-4752285B373A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Edge Shelf contributors
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\EdgeShelf
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=EdgeShelf-Setup-{#MyAppVersion}
SetupIconFile=..\EdgeShelf\Assets\EdgeShelf.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start Edge Shelf when I sign in to Windows"; GroupDescription: "Additional options:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "EdgeShelf"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "EdgeShelf"; Flags: deletevalue; Tasks: not startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
