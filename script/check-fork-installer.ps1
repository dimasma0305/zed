[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$destination = Join-Path $env:RUNNER_TEMP "Zed Fork installer check $([Guid]::NewGuid())"
$userPathBefore = [string](Get-ItemPropertyValue -Path HKCU:\Environment -Name Path -ErrorAction SilentlyContinue)

function Run-Setup([string]$Executable, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru
    if (-not $process.WaitForExit(120000)) {
        $process.Kill()
        throw 'Installer process timed out'
    }
    if ($process.ExitCode -ne 0) { throw "Installer process failed: $($process.ExitCode)" }
}

try {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS=desktopicon,contextmenu,addtopath', "/DIR=`"$destination`"")
    Run-Setup $installerPath $arguments
    $sentinel = Join-Path $destination 'preserve-user-file.txt'
    'Keep this file during an upgrade' | Set-Content -LiteralPath $sentinel
    Run-Setup $installerPath $arguments
    if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Upgrade removed an unrelated user file' }
    foreach ($file in Get-ChildItem -LiteralPath $package -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($package, $file.FullName)
        $installed = Join-Path $destination $relative
        if (-not (Test-Path -LiteralPath $installed)) { throw "Installer omitted $relative" }
        if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "Installed file differs: $relative"
        }
    }
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Zed Fork.lnk'
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Start menu shortcut was not created' }
    if (-not (Test-Path -LiteralPath 'HKCU:\Software\Classes\*\shell\ZedFork\command')) {
        throw 'File Explorer context menu was not registered'
    }
    $pathAfter = [string](Get-ItemPropertyValue -Path HKCU:\Environment -Name Path)
    if ($pathAfter.Split(';') -notcontains (Join-Path $destination 'bin')) { throw 'CLI directory was not added to PATH' }
} finally {
    $uninstaller = Join-Path $destination 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        Run-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    }
}
if (Test-Path -LiteralPath (Join-Path $destination 'zed.exe')) { throw 'Uninstall left the application installed' }
$userPathAfter = [string](Get-ItemPropertyValue -Path HKCU:\Environment -Name Path -ErrorAction SilentlyContinue)
if ($userPathAfter -cne $userPathBefore) { throw 'Uninstall changed unrelated PATH entries' }
if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Uninstall removed an unrelated user file' }
Remove-Item -LiteralPath $destination -Recurse -Force
Write-Output 'Verified per-user installation, upgrade, packaged file hashes, shortcuts, PATH, context menu, and uninstall'
