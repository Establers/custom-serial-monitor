#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$app = (Resolve-Path -LiteralPath $AppPath).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/native-background-smoke' }
$output = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$profile = Join-Path $env:LOCALAPPDATA 'SerialMonitor/profiles/default.json'
if (-not (Test-Path -LiteralPath $profile)) { throw 'Run the app once to create its profile before this native smoke test.' }
$profileHash = (Get-FileHash -LiteralPath $profile).Hash
$project = Join-Path $PSScriptRoot 'native-background-smoke/NativeBackgroundHook.csproj'
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Native startup hook build failed.' }
$hook = Join-Path $PSScriptRoot 'native-background-smoke/bin/Release/net8.0-windows10.0.19041.0/NativeBackgroundHook.dll'
$result = Join-Path $output 'result.txt'
$source = Join-Path $output 'source.png'
$fixture = Join-Path ($profile + '.backgrounds') ('native-background-smoke-' + [guid]::NewGuid().ToString('N') + ' 한글 #.png')
[IO.File]::WriteAllBytes($source, [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAJUlEQVR4AYXBAQEAIAyAMKSF/UxsId9AtrXPfXxIkCBBggQJEgZigwJeitSBCAAAAABJRU5ErkJggg=='))
$start = [Diagnostics.ProcessStartInfo]::new($app)
$start.UseShellExecute = $false
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.WorkingDirectory = Split-Path -Parent $app
$start.Environment['DOTNET_STARTUP_HOOKS'] = $hook
$start.Environment['SERIAL_BG_NATIVE_RESULT'] = $result
$start.Environment['SERIAL_BG_NATIVE_CUSTOM'] = $fixture
$start.Environment['SERIAL_BG_NATIVE_CUSTOM_SOURCE'] = $source
$process = $null
try {
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(60000)) { throw 'Native background test timed out.' }
    if ($process.ExitCode -ne 0) { throw (Get-Content -LiteralPath $result -Raw) }
    $expected = @{
        'bundled-50' = @('true', '0.5', 'serialmonitor-background-bundled.local')
        'custom-50' = @('true', '0.5', 'serialmonitor-background.local')
        'custom-100' = @('false', '0', 'serialmonitor-background.local')
        'custom-0' = @('true', '1', 'serialmonitor-background.local')
        'return-bundled-50' = @('true', '0.5', 'serialmonitor-background-bundled.local')
        'disabled' = @('false', '0.5', '')
        'alignment-initial' = @('true', '1', 'serialmonitor-background-bundled.local')
        'alignment-narrow' = @('true', '1', 'serialmonitor-background-bundled.local')
        'alignment-wide' = @('true', '1', 'serialmonitor-background-bundled.local')
        'alignment-collapsed' = @('true', '1', 'serialmonitor-background-bundled.local')
    }
    foreach ($shape in @('portrait', 'wide')) {
        foreach ($position in 0..8) {
            $expected["alignment-cover-$shape-$position"] = @('true', '1', 'serialmonitor-background-bundled.local')
        }
        $expected["alignment-contain-$shape"] = @('true', '1', 'serialmonitor-background-bundled.local')
    }
    foreach ($case in $expected.Keys) {
        $snapshot = Get-Content -LiteralPath ($result + '.' + $case + '.json') -Raw | ConvertFrom-Json
        $state = $snapshot.state | ConvertFrom-Json
        if ($snapshot.error) { throw "${case}: $($snapshot.error)" }
        if ($state.visible -ne $expected[$case][0] -or $state.opacity -ne $expected[$case][1]) {
            throw "${case}: unexpected image visibility/opacity: $($snapshot.state)"
        }
        if ($case -ne 'disabled' -and ($state.loaded -le 0 -or ([uri]$state.source).Host -ne $expected[$case][2])) {
            throw "${case}: native image resource did not load: $($snapshot.state)"
        }
        if ($state.text -notlike '*Native WinUI background test - opaque text*') { throw "${case}: retained text was lost." }
        if ($case -like 'alignment-*') {
            if ($null -eq $snapshot.alignmentError -or $snapshot.alignmentError -gt 8) {
                throw "${case}: native XAML/WebView pixels do not align (mean RGB error $($snapshot.alignmentError))."
            }
            Write-Output "PASS: $case (native image crop, mean RGB error $($snapshot.alignmentError))"
            continue
        }
        Write-Output "PASS: $case (native WebView2 image + retained text)"
    }
    if ((Get-FileHash -LiteralPath $profile).Hash -ne $profileHash) { throw 'The shared profile changed during the test.' }
    Write-Output "Native screenshots and state: $output"
} finally {
    # This is only the process started above. The user's existing app stays open.
    if ($process -and -not $process.HasExited) { Stop-Process -InputObject $process }
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture }
}
