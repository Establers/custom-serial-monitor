[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SerialNumber,
    [switch]$ReplaceFirmware,
    [string]$CubeIdeRoot = $env:STM32_CUBE_IDE,
    [string]$CubeF1Root = $env:STM32_CUBE_F1,
    [ValidatePattern('^build(?:-[A-Za-z0-9_-]+)?$')][string]$BuildDirectory = 'build'
)
. (Join-Path $PSScriptRoot 'tools.ps1')
if (-not $ReplaceFirmware) { throw 'This replaces target MCU firmware. Review build/manifest.json, then explicitly pass -ReplaceFirmware. No device operation performed.' }
$labRoot = Split-Path -Parent $PSScriptRoot
$toolset = Find-LabTools -CubeIdeRoot $CubeIdeRoot -CubeF1Root $CubeF1Root
if (-not $toolset.Programmer) { throw 'STM32CubeProgrammer CLI is missing.' }
$binary = Join-Path $labRoot "$BuildDirectory\serial-lab.bin"
$manifestPath = Join-Path $labRoot "$BuildDirectory\manifest.json"
if (-not (Test-Path -LiteralPath $binary) -or -not (Test-Path -LiteralPath $manifestPath)) { throw 'Run scripts/build.ps1 first.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.sha256 -ne (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash) { throw 'Binary does not match build manifest; rebuild first.' }
# Require explicit probe identity, and confirm target family + density before writing.
$connection = @('-c','port=SWD',('sn=' + $SerialNumber),'mode=UR','freq=1000')
$probeOutput = & $toolset.Programmer @connection '-r32' '0xE0042000' '0x4' '-r16' '0x1FFFF7E0' '0x2' 2>&1
$probeCode = $LASTEXITCODE
$probeText = $probeOutput -join "`n"
Write-Host $probeText
if ($probeCode -ne 0 -or $probeText -notmatch '(?im)0xE0042000\s*:\s*[0-9a-f]*410\b' -or $probeText -notmatch '(?im)0x1FFFF7E0\s*:\s*0080\b') {
    throw 'Target must be STM32F103 medium-density (device ID 0x410), 128KiB flash. Refusing to write.'
}
$backupDir = Join-Path $labRoot 'backups'
$null = New-Item -ItemType Directory -Path $backupDir -Force
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$backup = Join-Path $backupDir ("$SerialNumber-$stamp.bin")
Invoke-LabNative $toolset.Programmer ($connection + @('-u','0x08000000','0x20000',$backup))
if (-not (Test-Path -LiteralPath $backup) -or (Get-Item -LiteralPath $backup).Length -ne 131072) {
    throw 'Full 128KiB flash backup failed; target has NOT been overwritten.'
}
Get-FileHash -LiteralPath $backup -Algorithm SHA256 | Format-List
Write-Host "Original firmware backup: $backup"
# No mass erase, option-byte changes, RDP unlock, or ST-LINK firmware upgrade.
Invoke-LabNative $toolset.Programmer ($connection + @('-d',$binary,'0x08000000','-v','-rst'))
Write-Host 'Flashed and verified. Serial Monitor: COM3, 115200, 8N1, no handshake, UTF-8, CRLF.'
