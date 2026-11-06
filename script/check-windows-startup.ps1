[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Application,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$applicationPath = (Resolve-Path -LiteralPath $Application).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$profile = Join-Path $output 'profile'
$config = Join-Path $profile 'config'
New-Item -ItemType Directory -Path $config -Force | Out-Null
'{"telemetry":{"metrics":false,"diagnostics":false},"auto_update":false}' | Set-Content -LiteralPath (Join-Path $config 'settings.json')
$document = Join-Path $output 'startup-smoke.txt'
'Zed editor startup check' | Set-Content -LiteralPath $document
$previousGpuOverride = $env:ZED_ALLOW_EMULATED_GPU
$previousBacktrace = $env:RUST_BACKTRACE
$process = $null

function Assert-Running {
    $process.Refresh()
    if ($process.HasExited) {
        $code = ([uint32]([int64]$process.ExitCode -band 0xffffffffL)).ToString('X8')
        throw "Zed exited before completing GUI startup (exit code 0x$code)"
    }
}

try {
    # Hosted Windows runners can have a software adapter; exercise the full GUI anyway.
    $env:ZED_ALLOW_EMULATED_GPU = '1'
    $env:RUST_BACKTRACE = 'full'
    $arguments = @('--foreground', '--user-data-dir', "`"$profile`"", "`"$document`"")
    $process = Start-Process -FilePath $applicationPath -ArgumentList $arguments -PassThru -RedirectStandardOutput (Join-Path $output 'stdout.txt') -RedirectStandardError (Join-Path $output 'stderr.txt')
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        Start-Sleep -Milliseconds 500
        Assert-Running
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Zed did not open a visible editor window within 90 seconds' }
    Start-Sleep -Seconds 10
    Assert-Running
    $title = $process.MainWindowTitle
    if ($title -match 'failed to launch|unsupported GPU') { throw "Zed opened an error window: $title" }
    "Opened editor window: $title" | Set-Content -LiteralPath (Join-Path $output 'result.txt')
    Write-Output "Verified the packaged editor opens a window and remains running: $title"
} finally {
    if ($process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id -Force }
    }
    $log = Join-Path $profile 'logs/Zed.log'
    if (Test-Path -LiteralPath $log) {
        Copy-Item -LiteralPath $log -Destination (Join-Path $output 'Zed.log')
        Get-Content -LiteralPath $log -Tail 100 | Write-Output
    }
    foreach ($name in @('stdout.txt', 'stderr.txt')) {
        $file = Join-Path $output $name
        if (Test-Path -LiteralPath $file) { Get-Content -LiteralPath $file -Tail 100 | Write-Output }
    }
    $env:ZED_ALLOW_EMULATED_GPU = $previousGpuOverride
    $env:RUST_BACKTRACE = $previousBacktrace
}
