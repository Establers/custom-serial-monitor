[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $AppPath).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '../artifacts/search-native' }
$output = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$profile = Join-Path $env:LOCALAPPDATA 'SerialMonitor/profiles/default.json'
$profileHash = if (Test-Path -LiteralPath $profile) { (Get-FileHash -LiteralPath $profile).Hash }
dotnet build (Join-Path $PSScriptRoot 'native-search-smoke/NativeSearchHook.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Search test hook build failed.' }
$result = Join-Path $output 'result.json'
$start = [Diagnostics.ProcessStartInfo]::new($app)
$start.UseShellExecute = $false
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.WorkingDirectory = Split-Path -Parent $app
$start.Environment['DOTNET_STARTUP_HOOKS'] = Join-Path $PSScriptRoot 'native-search-smoke/bin/Release/net8.0-windows10.0.19041.0/NativeSearchHook.dll'
$start.Environment['SERIAL_SEARCH_TEST_RESULT'] = $result
$process = $null
try {
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(120000)) { throw "Native search test timed out: $result" }
    if (-not (Test-Path -LiteralPath $result)) { throw "No search test report (exit $($process.ExitCode))." }
    $report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if ($report.error) { throw $report.error }
    if ($process.ExitCode -ne 0 -or $report.checks.Count -lt 30) { throw 'Native search checks incomplete.' }
    if ($profileHash -and (Get-FileHash -LiteralPath $profile).Hash -ne $profileHash) { throw 'Shared profile changed during the test.' }
    Write-Output "PASS: $($report.checks.Count) native search checks. Report and screenshots: $result"
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -InputObject $process -Force }
    if ($process) { $process.Dispose() }
}
