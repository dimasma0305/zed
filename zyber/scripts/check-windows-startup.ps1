param([Parameter(Mandatory)][string]$Application)
$ErrorActionPreference = 'Stop'
$applicationPath = (Resolve-Path $Application).Path
$testDirectory = Join-Path $env:TEMP ("zyber-startup-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$document = Join-Path $testDirectory 'startup.cs'
Set-Content -LiteralPath $document -Value '// Zyber native startup test'
$process = $null
try {
    $process = Start-Process -FilePath $applicationPath -ArgumentList "`"$document`"" -PassThru
    $deadline = (Get-Date).AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "Zyber exited before opening a window: $($process.ExitCode)" }
    } until ($process.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw 'Zyber did not open a native window within 45 seconds.' }
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited) { throw "Zyber crashed after startup: $($process.ExitCode)" }
    if ($process.MainWindowTitle -notlike '*Zyber*') { throw "Unexpected window: $($process.MainWindowTitle)" }
    Write-Output "Native window opened and remained alive: $($process.MainWindowTitle)"
} finally {
    if ($process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id }
    }
    Remove-Item -LiteralPath $testDirectory -Recurse -Force
}
