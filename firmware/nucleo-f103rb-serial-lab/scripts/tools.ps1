Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-LabTools {
    param([string]$CubeIdeRoot = $env:STM32_CUBE_IDE, [string]$CubeF1Root = $env:STM32_CUBE_F1)
    if (-not $CubeIdeRoot) {
        $candidates = @(Get-ChildItem -LiteralPath 'C:\ST' -Directory -Filter 'STM32CubeIDE*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
        if ($candidates.Count -gt 0) { $CubeIdeRoot = Join-Path $candidates[0].FullName 'STM32CubeIDE' }
    }
    if (-not $CubeIdeRoot -or -not (Test-Path -LiteralPath $CubeIdeRoot)) {
        throw 'STM32CubeIDE not found. Install from https://www.st.com/en/development-tools/stm32cubeide.html or set STM32_CUBE_IDE to the folder containing plugins.'
    }
    $plugins = Join-Path $CubeIdeRoot 'plugins'
    $gcc = @(Get-ChildItem -LiteralPath $plugins -Filter 'arm-none-eabi-gcc.exe' -Recurse -File | Sort-Object FullName -Descending)
    $programmer = @(Get-ChildItem -LiteralPath $plugins -Filter 'STM32_Programmer_CLI.exe' -Recurse -File | Sort-Object FullName -Descending)
    if (-not $CubeF1Root) {
        $packages = @(Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE 'STM32Cube\Repository') -Directory -Filter 'STM32Cube_FW_F1_V*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
        if ($packages.Count -gt 0) { $CubeF1Root = $packages[0].FullName }
    }
    if ($gcc.Count -eq 0) { throw 'ARM GCC is missing from CubeIDE. Reinstall its STM32 MCU tools.' }
    if (-not $CubeF1Root -or -not (Test-Path -LiteralPath (Join-Path $CubeF1Root 'Drivers\CMSIS\Include\core_cm3.h'))) {
        throw 'STM32CubeF1 SDK missing. CubeIDE: Help > Manage embedded software packages > STM32F1 > Install. Or set STM32_CUBE_F1.'
    }
    [pscustomobject]@{
        Ide = $CubeIdeRoot
        Gcc = $gcc[0].FullName
        Bin = $gcc[0].DirectoryName
        Sdk = $CubeF1Root
        Programmer = $(if ($programmer.Count) { $programmer[0].FullName } else { $null })
    }
}

function Invoke-LabNative {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $Executable" }
}
