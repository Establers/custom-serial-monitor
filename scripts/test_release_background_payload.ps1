[CmdletBinding()]
param([string]$PortableDirectory)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PortableDirectory) { $PortableDirectory = Join-Path $repoRoot 'release\SerialMonitorPortable' }
$payload = [IO.Path]::GetFullPath($PortableDirectory)
$checker = Join-Path $PSScriptRoot 'check_release_payload.ps1'
$null = & $checker -PortableDirectory $payload
Write-Output 'PASS: complete current payload'

# A tiny owned fixture is sufficient: asset validation must reject the broken
# files before probing EXEs or launching the bundled CLI.
$fixture = Join-Path $repoRoot ('artifacts\release-background-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'Assets\FunBackgrounds') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $payload 'Assets\xterm') -Destination (Join-Path $fixture 'Assets') -Recurse
Copy-Item -LiteralPath (Join-Path $payload 'Assets\FunBackgrounds\default_cute_bg.jpg') `
    -Destination (Join-Path $fixture 'Assets\FunBackgrounds\default_cute_bg.jpg')

foreach ($relative in @('Assets\xterm\index.html', 'Assets\xterm\terminal-background.js',
    'Assets\FunBackgrounds\default_cute_bg.jpg')) {
    $path = Join-Path $fixture $relative
    $original = [IO.File]::ReadAllBytes($path)
    foreach ($mode in @('missing', 'stale')) {
        try {
            if ($mode -eq 'missing') { Remove-Item -LiteralPath $path }
            else { [IO.File]::WriteAllBytes($path, [Text.Encoding]::UTF8.GetBytes('stale test asset')) }
            try {
                $null = & $checker -PortableDirectory $fixture
                throw "Broken asset was accepted: $mode $relative"
            } catch {
                $expected = 'Release payload is missing or stale: SerialMonitor.WinUI\' + $relative
                if (-not $_.Exception.Message.Contains($expected)) { throw }
            }
            Write-Output "PASS: rejected $mode $relative"
        } finally {
            [IO.File]::WriteAllBytes($path, $original)
        }
    }
}
Write-Output 'Release background validation: 7 checks passed; original payload unchanged.'
