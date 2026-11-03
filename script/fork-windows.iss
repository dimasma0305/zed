#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef ReleaseVersion
  #error ReleaseVersion is required
#endif

#define ApplicationVersion GetFileVersion(PackageDir + "\zed.exe")

[Setup]
AppId={{2DB0DA96-CA55-49BB-AF4F-64AF36A86712}
AppName=Zed
AppVerName=Zed
UninstallDisplayName=Zed
AppVersion={#ApplicationVersion}
AppPublisher=dimasma0305
AppPublisherURL=https://github.com/dimasma0305/zed
AppSupportURL=https://github.com/dimasma0305/zed/issues
AppUpdatesURL=https://github.com/dimasma0305/zed/releases
DefaultDirName={autopf}\Zed
DefaultGroupName=Zed
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.16299
WizardStyle=modern
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseName}
SetupIconFile={#ResourcesDir}\app-icon.ico
UninstallDisplayIcon={app}\zed.exe
LicenseFile={#PackageDir}\LICENSE-GPL
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ChangesEnvironment=yes
ChangesAssociations=yes
SetupMutex=ZedStableSetup
AppMutex=Zed-Editor-Stable-Instance-Mutex

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "contextmenu"; Description: "Add Open with Zed to File Explorer"; Flags: unchecked
Name: "addtopath"; Description: "Add the zed command to your user PATH"

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Zed"; Filename: "{app}\zed.exe"; AppUserModelID: "ZedIndustries.Zed"
Name: "{userdesktop}\Zed"; Filename: "{app}\zed.exe"; AppUserModelID: "ZedIndustries.Zed"; Tasks: desktopicon

[Run]
Filename: "{app}\zed.exe"; Description: "Launch Zed"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Classes\*\shell\Zed"; ValueType: string; ValueData: "Open with Zed"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Zed"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\zed.exe"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Zed\command"; ValueType: string; ValueData: """{app}\zed.exe"" ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Zed"; ValueType: string; ValueData: "Open with Zed"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Zed\command"; ValueType: string; ValueData: """{app}\zed.exe"" ""%1"""; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\Applications\Zed.exe\shell\open\command"; ValueType: string; ValueData: """{app}\zed.exe"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\zed"; ValueType: string; ValueData: "URL:zed Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\zed"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\zed\shell\open\command"; ValueType: string; ValueData: """{app}\zed.exe"" ""%1"""

[Code]
const InstallerKey = 'Software\dimasma0305\Zed\Installer';

function ContainsPathEntry(Path, Entry: String): Boolean;
var Remaining, Part: String; Separator: Integer;
begin
  Result := False;
  Remaining := Path + ';';
  while Pos(';', Remaining) > 0 do begin
    Separator := Pos(';', Remaining);
    Part := Copy(Remaining, 1, Separator - 1);
    Delete(Remaining, 1, Separator);
    if CompareText(Part, Entry) = 0 then begin
      Result := True;
      Exit;
    end;
  end;
end;

function WithoutPathEntry(Path, Entry: String): String;
var Remaining, Part: String; Separator: Integer; First: Boolean;
begin
  Result := '';
  First := True;
  Remaining := Path + ';';
  while Pos(';', Remaining) > 0 do begin
    Separator := Pos(';', Remaining);
    Part := Copy(Remaining, 1, Separator - 1);
    Delete(Remaining, 1, Separator);
    if CompareText(Part, Entry) <> 0 then begin
      if not First then Result := Result + ';';
      Result := Result + Part;
      First := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Path, Entry: String;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then begin
    Entry := ExpandConstant('{app}\bin');
    RegQueryStringValue(HKCU, 'Environment', 'Path', Path);
    if not ContainsPathEntry(Path, Entry) then begin
      if Path <> '' then Path := Path + ';';
      if not RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Path + Entry) then
        RaiseException('Could not update your user PATH');
    end;
    if not RegWriteStringValue(HKCU, InstallerKey, 'ManagedPath', Entry) then
      RaiseException('Could not record the installed PATH entry');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Path, Entry: String;
begin
  if (CurUninstallStep = usUninstall) and RegQueryStringValue(HKCU, InstallerKey, 'ManagedPath', Entry) then begin
    if RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
      RegWriteExpandStringValue(HKCU, 'Environment', 'Path', WithoutPathEntry(Path, Entry));
    RegDeleteValue(HKCU, InstallerKey, 'ManagedPath');
    RegDeleteKeyIfEmpty(HKCU, InstallerKey);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Uninstaller: String; ExitCode: Integer;
begin
  Result := '';
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{413FF1A9-1C59-47A8-997E-E3A3DA40D694}_is1', 'UninstallString', Uninstaller) then begin
    if not Exec(RemoveQuotes(Uninstaller), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      Result := 'Could not remove the previous Zed Fork installation'
    else if ExitCode <> 0 then
      Result := 'The previous Zed Fork uninstaller failed. Close Zed and retry setup.';
  end;
end;
