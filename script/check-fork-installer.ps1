[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$testRoot = Join-Path $env:RUNNER_TEMP "Zed installer check $([Guid]::NewGuid())"
$destination = Join-Path $testRoot 'Existing Zed'
$oldFork = Join-Path $testRoot 'Previous Zed Fork'
$userPathBefore = [string](Get-ItemPropertyValue -Path HKCU:\Environment -Name Path -ErrorAction SilentlyContinue)
$uninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
$originalKey = Join-Path $uninstallRoot '{2DB0DA96-CA55-49BB-AF4F-64AF36A86712}_is1'
$oldForkKey = Join-Path $uninstallRoot '{413FF1A9-1C59-47A8-997E-E3A3DA40D694}_is1'
$settingsFile = Join-Path $env:APPDATA 'Zed/settings.json'
$settingsExisted = Test-Path -LiteralPath $settingsFile
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
if (-not $settingsExisted) {
    New-Item -ItemType Directory -Path (Split-Path $settingsFile) -Force | Out-Null
    '{"theme":"existing user setting"}' | Set-Content -LiteralPath $settingsFile
}
$settingsHash = (Get-FileHash -LiteralPath $settingsFile).Hash

function Run-Setup([string]$Executable, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer process failed: $($process.ExitCode)" }
}

function Install-Fixture([string]$Name, [string]$AppId, [string]$Directory) {
    $oldBinary = Join-Path $testRoot 'old-binary.txt'
    'old application binary' | Set-Content -LiteralPath $oldBinary
    $script = Join-Path $testRoot "$Name.iss"
    @"
[Setup]
AppId=$AppId
AppName=$Name
AppVersion=0.0.1
DefaultDirName=$Directory
DefaultGroupName=Zed
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=$testRoot
OutputBaseFilename=$Name-fixture
DisableProgramGroupPage=yes
[Files]
Source: "$oldBinary"; DestDir: "{app}"; DestName: "zed.exe"
Source: "$oldBinary"; DestDir: "{app}\bin"; DestName: "zed.exe"
"@ | Set-Content -LiteralPath $script
    $compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
    & $compiler $script
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile the previous-install fixture' }
    Run-Setup (Join-Path $testRoot "$Name-fixture.exe") @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER')
}

try {
    Install-Fixture 'Zed' '{{2DB0DA96-CA55-49BB-AF4F-64AF36A86712}' $destination
    Install-Fixture 'ZedFork' '{{413FF1A9-1C59-47A8-997E-E3A3DA40D694}' $oldFork
    # The existing installer already adds its CLI directory to PATH.
    $existingBin = Join-Path $destination 'bin'
    $originalPath = if ($userPathBefore) { "$userPathBefore;$existingBin" } else { $existingBin }
    Set-ItemProperty -Path HKCU:\Environment -Name Path -Value $originalPath
    $sentinel = Join-Path $destination 'preserve-user-file.txt'
    'Keep this file during an upgrade' | Set-Content -LiteralPath $sentinel
    # Omitting /DIR exercises Inno's discovery of the original Zed installation.
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS=desktopicon,contextmenu,addtopath')
    Run-Setup $installerPath $arguments
    Run-Setup $installerPath $arguments
    $registration = Get-ItemProperty -LiteralPath $originalKey
    if ($registration.DisplayName -cne 'Zed') { throw 'Installed application is not named Zed' }
    if ($registration.InstallLocation.TrimEnd('\') -ine $destination) { throw 'Setup did not reuse the original installation directory' }
    if (Test-Path -LiteralPath $oldForkKey) { throw 'The previous Zed Fork installation was not removed' }
    if (Test-Path -LiteralPath (Join-Path $oldFork 'zed.exe')) { throw 'The separate fork executable remains installed' }
    if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Upgrade removed an unrelated user file' }
    foreach ($file in Get-ChildItem -LiteralPath $package -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($package, $file.FullName)
        $installed = Join-Path $destination $relative
        if (-not (Test-Path -LiteralPath $installed)) { throw "Installer omitted $relative" }
        if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "Installed file differs: $relative"
        }
    }
    & "$PSScriptRoot/check-windows-startup.ps1" -Application (Join-Path $destination 'zed.exe') -OutputDirectory windows-startup-qa
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Zed/Zed.lnk'
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Zed Start menu shortcut was not created' }
    if (-not (Test-Path -LiteralPath 'HKCU:\Software\Classes\*\shell\Zed\command')) {
        throw 'Open with Zed was not registered'
    }
    $pathAfter = [string](Get-ItemPropertyValue -Path HKCU:\Environment -Name Path)
    if (@($pathAfter.Split(';') | Where-Object { $_ -ieq $existingBin }).Count -ne 1) { throw 'The original CLI PATH entry was duplicated or removed' }
    if ((Get-FileHash -LiteralPath $settingsFile).Hash -ne $settingsHash) { throw 'Installation changed existing Zed settings' }
} finally {
    foreach ($directory in @($destination, $oldFork)) {
        $uninstaller = Join-Path $directory 'unins000.exe'
        if (Test-Path -LiteralPath $uninstaller) {
            Run-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        }
    }
}
if (Test-Path -LiteralPath (Join-Path $destination 'zed.exe')) { throw 'Uninstall left the application installed' }
$userPathAfter = [string](Get-ItemPropertyValue -Path HKCU:\Environment -Name Path -ErrorAction SilentlyContinue)
if ($userPathAfter -cne $userPathBefore) { throw 'Uninstall changed unrelated PATH entries' }
if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Uninstall removed an unrelated user file' }
if ((Get-FileHash -LiteralPath $settingsFile).Hash -ne $settingsHash) { throw 'Uninstall changed existing Zed settings' }
if (-not $settingsExisted) { Remove-Item -LiteralPath $settingsFile }
Remove-Item -LiteralPath $testRoot -Recurse -Force
Write-Output 'Verified replacement of the original Zed, removal of the separate fork, settings preservation, CLI replacement, upgrade, and uninstall'
