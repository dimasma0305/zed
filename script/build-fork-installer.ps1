[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$repository = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($required in @('zed.exe', 'cli.exe', 'conpty.dll', 'OpenConsole.exe', 'LICENSE-GPL', 'remote-servers/manifest.json', 'bin/zed.cmd', 'bin/zed.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $required))) {
        throw "Missing installer input: $required"
    }
}
$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Inno Setup 6 is required to build the installer'
}
& $compiler "/DPackageDir=$package" "/DOutputDir=$output" "/DOutputBaseName=zed-windows-x64-v$Version-setup" "/DReleaseVersion=$Version" "/DResourcesDir=$repository/crates/zed/resources/windows" "$PSScriptRoot/fork-windows.iss"
if ($LASTEXITCODE -ne 0) { throw "Installer compiler failed: $LASTEXITCODE" }
$installer = Join-Path $output "zed-windows-x64-v$Version-setup.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw 'Installer was not created' }
$digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash.ToLowerInvariant()
"$digest  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding utf8NoBOM
Write-Output $installer
