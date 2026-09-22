#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\artifacts\installer"
#endif

#define MyAppName "A-Note"
#define MyAppPublisher "Contraș Adrian"
#define MyAppExeName "A-Note.exe"
#define MyAppId "{{E0C05CA6-240B-43D3-9AF6-8ED98B3942E2}"
#define MyProjectRoot ".."
#define MyPublishDir MyProjectRoot + "\artifacts\publish\win-x64"
#define MyIconFile MyProjectRoot + "\assets\logo\A-Note.ico"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\A-Note
DefaultGroupName=A-Note
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#MyOutputDir}
OutputBaseFilename=A-Note-Setup-{#MyAppVersion}
SetupIconFile={#MyIconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=A-Note Setup
VersionInfoProductName=A-Note
VersionInfoProductVersion={#MyAppVersion}
SetupLogging=yes

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\A-Note"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\A-Note"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch A-Note"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
