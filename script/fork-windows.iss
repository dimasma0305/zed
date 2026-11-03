#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef ReleaseVersion
  #error ReleaseVersion is required
#endif

[Setup]
AppId={{413FF1A9-1C59-47A8-997E-E3A3DA40D694}
AppName=Zed Fork
AppVersion={#ReleaseVersion}
AppPublisher=dimasma0305
AppPublisherURL=https://github.com/dimasma0305/zed
AppSupportURL=https://github.com/dimasma0305/zed/issues
AppUpdatesURL=https://github.com/dimasma0305/zed/releases
DefaultDirName={localappdata}\Programs\Zed Fork
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
SetupMutex=ZedForkSetup

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "contextmenu"; Description: "Add Open with Zed Fork to File Explorer"; Flags: unchecked
Name: "addtopath"; Description: "Add zed and zed-fork commands to your user PATH"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Zed Fork"; Filename: "{app}\zed.exe"
Name: "{userdesktop}\Zed Fork"; Filename: "{app}\zed.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\zed.exe"; Description: "Launch Zed Fork"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Classes\*\shell\ZedFork"; ValueType: string; ValueData: "Open with Zed Fork"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\ZedFork"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\zed.exe"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\ZedFork\command"; ValueType: string; ValueData: """{app}\zed.exe"" ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ZedFork"; ValueType: string; ValueData: "Open with Zed Fork"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\ZedFork\command"; ValueType: string; ValueData: """{app}\zed.exe"" ""%1"""; Tasks: contextmenu

[Code]
const InstallerKey = 'Software\dimasma0305\ZedFork\Installer';

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
      RegWriteStringValue(HKCU, InstallerKey, 'ManagedPath', Entry);
    end;
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
