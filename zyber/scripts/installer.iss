#ifndef PackageDir
  #error PackageDir must point to the published win-x64 directory
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
[Setup]
AppId={{794C1B14-768B-48BD-BAF8-889814372BD6}
AppName=Zyber
AppVersion=0.1.0
AppPublisher=Zyber contributors
DefaultDirName={localappdata}\Programs\Zyber
DefaultGroupName=Zyber
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=zyber-windows-x64-0.1.0-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Zyber.exe
LicenseFile={#PackageDir}\LICENSE
CloseApplications=yes
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Zyber"; Filename: "{app}\Zyber.exe"
Name: "{autodesktop}\Zyber"; Filename: "{app}\Zyber.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\Zyber.exe"; Description: "Open Zyber"; Flags: nowait postinstall skipifsilent
