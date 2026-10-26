#define MyAppName "Serial Monitor"
#define MyAppExeName "SerialMonitor.WinUI.exe"
#define MyAppPublisher "Serial Monitor"
#define MyAppVersion "1.5.1"
#define BuildTimestamp GetDateTimeString('yyyymmdd_hhnn', '', '')
#ifndef PortableSourceDir
  #define PortableSourceDir "..\release\SerialMonitorPortable"
#endif

[Setup]
AppId={{BA846EA7-2A2B-46B6-A501-3D79E6C4908B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\SerialMonitor
DisableDirPage=no
UsePreviousAppDir=yes
AlwaysShowDirOnReadyPage=yes
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=auto
OutputDir=..\release\installer
OutputBaseFilename=SerialMonitorSetup_{#BuildTimestamp}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
SetupLogging=yes
LicenseFile=..\LICENSE
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PortableSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent unchecked
