[CmdletBinding()]
param([string]$CubeIdeRoot = $env:STM32_CUBE_IDE, [string]$CubeF1Root = $env:STM32_CUBE_F1,
    [ValidatePattern('^build(?:-[A-Za-z0-9_-]+)?$')][string]$BuildDirectory = 'build')
. (Join-Path $PSScriptRoot 'tools.ps1')
$labRoot = Split-Path -Parent $PSScriptRoot
$toolset = Find-LabTools -CubeIdeRoot $CubeIdeRoot -CubeF1Root $CubeF1Root
$buildDir = Join-Path $labRoot $BuildDirectory
$null = New-Item -ItemType Directory -Path $buildDir -Force
$device = Join-Path $toolset.Sdk 'Drivers\CMSIS\Device\ST\STM32F1xx'
$projectFiles = @(Get-ChildItem -LiteralPath (Join-Path $labRoot 'src'),(Join-Path $labRoot 'include') -File |
    Where-Object { $_.Extension -in '.c','.h' }) + @(Get-Item -LiteralPath (Join-Path $labRoot 'stm32f103rb.ld'),$PSCommandPath)
$sources = @($projectFiles | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($labRoot.Length + 1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$sdkFiles = @(Get-ChildItem -LiteralPath (Join-Path $device 'Include'),(Join-Path $toolset.Sdk 'Drivers\CMSIS\Include') -File -Filter '*.h') +
    @(Get-Item -LiteralPath (Join-Path $device 'Source\Templates\system_stm32f1xx.c'),(Join-Path $device 'Source\Templates\gcc\startup_stm32f103xb.s'))
$sdkInputs = @($sdkFiles | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($toolset.Sdk.Length + 1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
function Hash-Text([string]$Text) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-','') }
    finally { $hasher.Dispose() }
}
$compilerVersion = (& $toolset.Gcc '--version') -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect ARM GCC.' }
$sourceDigest = Hash-Text ($sources | ConvertTo-Json -Depth 4 -Compress)
$optionsIdentity = 'cortex-m3;mthumb;c11;Os;g3;ffunction-sections;fdata-sections;fstack-usage;Wstack-usage=2048;nano;nosys;nostartfiles;gc-sections'
$buildDigest = Hash-Text ($sourceDigest + "`n" + $compilerVersion + "`n" + $optionsIdentity + "`n" + ($sdkInputs | ConvertTo-Json -Depth 4 -Compress))
$buildId = $buildDigest.Substring(0,16)
[IO.File]::WriteAllText((Join-Path $buildDir 'lab_build_info.h'),("#define LAB_BUILD_ID `"$buildId`"`n"),[Text.UTF8Encoding]::new($false))
$versionText = [IO.File]::ReadAllText((Join-Path $labRoot 'include\lab_fixtures.h'))
if ($versionText -notmatch 'LAB_FIRMWARE_VERSION\s+"([^"]+)"') { throw 'Firmware version definition missing.' }
$firmwareVersion = $Matches[1]
$limitsText = [IO.File]::ReadAllText((Join-Path $labRoot 'include\lab.h'))
if ($limitsText -notmatch 'LAB_MAX_PAYLOAD\s+(\d+)u') { throw 'Payload size definition missing.' }
$maxPayload = [int]$Matches[1]
$common = @('-mcpu=cortex-m3','-mthumb','-std=c11','-Os','-g3','-ffunction-sections','-fdata-sections','-fstack-usage','-Wstack-usage=2048',
    '-Wall','-Wextra','-Werror','-DSTM32F103xB','-DLAB_TARGET_BUILD',('-I' + $buildDir),('-I' + (Join-Path $labRoot 'include')),
    ('-I' + (Join-Path $device 'Include')),('-I' + (Join-Path $toolset.Sdk 'Drivers\CMSIS\Include')))
$inputs = @(
    (Join-Path $labRoot 'src\main.c'),
    (Join-Path $labRoot 'src\lab.c'),
    (Join-Path $labRoot 'src\lab_fixtures.c'),
    (Join-Path $device 'Source\Templates\system_stm32f1xx.c'),
    (Join-Path $device 'Source\Templates\gcc\startup_stm32f103xb.s'))
$objects = @()
foreach ($source in $inputs) {
    $obj = Join-Path $buildDir ([IO.Path]::GetFileNameWithoutExtension($source) + '.o')
    Invoke-LabNative $toolset.Gcc ($common + @('-c',$source,'-o',$obj))
    $objects += $obj
}
$elf = Join-Path $buildDir 'serial-lab.elf'
$map = Join-Path $buildDir 'serial-lab.map'
Invoke-LabNative $toolset.Gcc (@('-mcpu=cortex-m3','-mthumb','--specs=nano.specs','--specs=nosys.specs',
    '-nostartfiles',('-T' + (Join-Path $labRoot 'stm32f103rb.ld')),
    ('-Wl,--gc-sections,--print-memory-usage,-Map=' + $map)) + $objects + @('-Wl,--start-group','-lc','-lm','-lnosys','-Wl,--end-group','-o',$elf))
foreach ($format in @(@('binary','bin'),@('ihex','hex'))) {
    Invoke-LabNative (Join-Path $toolset.Bin 'arm-none-eabi-objcopy.exe') @('-O',$format[0],$elf,(Join-Path $buildDir ('serial-lab.' + $format[1])))
}
Invoke-LabNative (Join-Path $toolset.Bin 'arm-none-eabi-size.exe') @($elf)
$manifest = [ordered]@{
    schemaVersion = 2; firmwareVersion = $firmwareVersion; buildId = $buildId
    maxBinaryPayloadBytes = $maxPayload; maxBinaryFrameBytes = $maxPayload + 11
    sourceSha256 = $sourceDigest; buildInputsSha256 = $buildDigest; compilerVersion = $compilerVersion
    sources = $sources; sdkInputs = $sdkInputs; optionsIdentity = $optionsIdentity
    board = 'NUCLEO-F103RB'; mcu = 'STM32F103RBT6'; protocol = 'SERIAL-LAB/1'
    flashAddress = '0x08000000'; flashBytes = 131072; ramBytes = 20480
    defaultPort = 'COM3'; baudRate = 115200; serial = '8N1 / no handshake / UTF-8'
    builtAt = [DateTimeOffset]::Now.ToString('o'); gcc = $toolset.Gcc; sdk = $toolset.Sdk
    sha256 = (Get-FileHash -LiteralPath (Join-Path $buildDir 'serial-lab.bin') -Algorithm SHA256).Hash
    binaryBytes = (Get-Item -LiteralPath (Join-Path $buildDir 'serial-lab.bin')).Length
    stackUsageBudgetPerFunctionBytes = 2048
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $buildDir 'manifest.json') -Encoding UTF8
Write-Host "Built: $elf"
Write-Host "Firmware $firmwareVersion / build ID $buildId (compare with device BUILD?)"
Write-Host 'Build does not connect to or modify the board.'
