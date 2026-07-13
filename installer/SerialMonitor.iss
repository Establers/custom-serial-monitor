#define MyAppName "Serial Monitor"
#define MyAppExeName "SerialMonitor.WinUI.exe"
#define MyAppPublisher "Serial Monitor"
#define MyAppVersion "1.0.1"
#define BuildTimestamp GetDateTimeString('yyyymmdd_hhnn', '', '')

[Setup]
AppId={{BA846EA7-2A2B-46B6-A501-3D79E6C4908B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\SerialMonitor
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

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\release\SerialMonitorPortable\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
