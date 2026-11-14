[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [ValidateRange(1, 30)][int]$TimeoutSeconds = 15,
    [ValidateRange(1, 15)][int]$ObserveSeconds = 5
)

$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $AppPath).Path
$diagnosticsRoot = Join-Path $env:LOCALAPPDATA 'SerialMonitor/diagnostics'
$profilePath = Join-Path $env:LOCALAPPDATA 'SerialMonitor/profiles/default.json'
$profileHash = if (Test-Path -LiteralPath $profilePath) { (Get-FileHash -LiteralPath $profilePath).Hash }
$testProcess = $null
$diagnostics = $null
try {
    # Launch only this test instance; do not send input or automate its UI.
    $testProcess = Start-Process -FilePath $app -WorkingDirectory (Split-Path -Parent $app) -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $readyAt = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $testProcess.Refresh()
        if (-not $diagnostics) {
            $diagnostics = Get-ChildItem -LiteralPath $diagnosticsRoot -Directory -Filter "instance-$($testProcess.Id)-*" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
        }
        if ($diagnostics) {
            $errorPath = Join-Path $diagnostics.FullName 'last_runtime_error.txt'
            if (Test-Path -LiteralPath $errorPath) { throw (Get-Content -LiteralPath $errorPath -Raw) }
        }
        if ($testProcess.HasExited) { throw "App exited during startup (exit code $($testProcess.ExitCode))." }
        if ($diagnostics -and $testProcess.MainWindowHandle -ne [IntPtr]::Zero -and $testProcess.Responding) {
            if (-not $readyAt) { $readyAt = [DateTime]::UtcNow }
            if (([DateTime]::UtcNow - $readyAt).TotalSeconds -ge $ObserveSeconds) {
                Write-Output "PASS: native app startup remained responsive for $ObserveSeconds seconds without runtime errors."
                Write-Output "App: $app"
                Write-Output "Diagnostics: $($diagnostics.FullName)"
                break
            }
        } else { $readyAt = $null }
        Start-Sleep -Milliseconds 200
    }
    if (-not $readyAt -or ([DateTime]::UtcNow - $readyAt).TotalSeconds -lt $ObserveSeconds) {
        throw 'Timed out waiting for a responsive application window.'
    }
} finally {
    # Stop only our child without the normal shutdown path saving shared preferences.
    if ($testProcess) {
        if (-not $testProcess.HasExited) { Stop-Process -InputObject $testProcess -Force }
        $testProcess.Dispose()
    }
}
if ($profileHash -and (Get-FileHash -LiteralPath $profilePath).Hash -ne $profileHash) {
    throw 'The shared profile changed during the startup test.'
}
