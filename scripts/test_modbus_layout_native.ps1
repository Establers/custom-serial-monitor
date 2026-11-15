[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [string]$OutputDirectory,
    [string]$CaseFilter
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$app = (Resolve-Path -LiteralPath $AppPath).Path
$managedApp = [IO.Path]::ChangeExtension($app, '.dll')
if (-not (Test-Path -LiteralPath $managedApp)) {
    $managedApp = Join-Path (Split-Path -Parent $app) 'SerialMonitor.WinUI.dll'
}
$payloadHashes = @(Get-FileHash -LiteralPath $app, $managedApp)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/modbus-layout-native' }
$output = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$profile = Join-Path $env:LOCALAPPDATA 'SerialMonitor/profiles/default.json'
$profileHash = if (Test-Path -LiteralPath $profile) { (Get-FileHash -LiteralPath $profile).Hash }
$project = Join-Path $PSScriptRoot 'native-modbus-layout-smoke/NativeModbusLayoutHook.csproj'
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Native Modbus layout hook build failed.' }
$hook = Join-Path $PSScriptRoot 'native-modbus-layout-smoke/bin/Release/net8.0-windows10.0.19041.0/NativeModbusLayoutHook.dll'
$result = Join-Path $output 'result.json'
$start = [Diagnostics.ProcessStartInfo]::new($app)
$start.UseShellExecute = $false
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.WorkingDirectory = Split-Path -Parent $app
$start.Environment['DOTNET_STARTUP_HOOKS'] = $hook
$start.Environment['SERIAL_MODBUS_LAYOUT_RESULT'] = $result
$start.Environment['SERIAL_MODBUS_LAYOUT_COMMANDS'] = Join-Path $repo 'firmware/nucleo-f103rb-serial-lab/presets/modbus-commands.json'
if ($CaseFilter) { $start.Environment['SERIAL_MODBUS_LAYOUT_FILTER'] = $CaseFilter }
$process = $null
try {
    $process = [Diagnostics.Process]::Start($start)
    # Includes every dialog footer's focus/default/press states at both window sizes.
    $timeout = if ($CaseFilter -like 'dialog-keyboard*') { 900000 } else { 300000 }
    if (-not $process.WaitForExit($timeout)) { throw "Layout test timed out. Partial report: $result" }
    if (-not (Test-Path -LiteralPath $result)) { throw "No native layout report was produced (exit $($process.ExitCode))." }
    $report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if ($report.fatal) { throw $report.fatal }
    if ($report.cases.Count -eq 0) { throw 'No native layout cases ran.' }
    $report.cases | ForEach-Object { Write-Output "CHECK: $($_.name) ($($_.width) x $($_.height))" }
    if ($report.issues.Count -gt 0) { $report.issues | Select-Object -First 12 | ForEach-Object { Write-Output "FAIL: $_" }; throw "Native layout found $($report.issues.Count) issue(s). Report: $result" }
    if ($process.ExitCode -ne 0) { throw "Native layout test exited with code $($process.ExitCode)." }
    foreach ($hash in $payloadHashes) {
        if ((Get-FileHash -LiteralPath $hash.Path).Hash -ne $hash.Hash) { throw 'Portable payload changed during the layout test.' }
    }
    if ($profileHash -and (Get-FileHash -LiteralPath $profile).Hash -ne $profileHash) { throw 'Shared profile changed during the test.' }
    $diagnostics = Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'SerialMonitor/diagnostics') -Directory -Filter "instance-$($process.Id)-*" | Select-Object -First 1
    if ($diagnostics -and (Test-Path -LiteralPath (Join-Path $diagnostics.FullName 'last_runtime_error.txt'))) {
        throw (Get-Content -LiteralPath (Join-Path $diagnostics.FullName 'last_runtime_error.txt') -Raw)
    }
    $payloadHashes | Select-Object Path,Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'payload-hashes.json')
    $formGridCount = @($report.cases | ForEach-Object { $_.formGrids }).Count
    Write-Output "PASS: $($report.cases.Count) native Modbus layout cases, $formGridCount shared form grids. Report: $result"
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -InputObject $process -Force }
    if ($process) { $process.Dispose() }
}
