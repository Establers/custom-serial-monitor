[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$StopRunning,
    [string]$Configuration = "Release",
    [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Find-InnoSetupCompiler {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = Resolve-FullPath $ExplicitPath
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            return $resolved
        }

        throw "ISCC.exe was not found at the supplied path: $resolved"
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 5\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 5\ISCC.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return $null
}

function Write-Sha256File {
    param([Parameter(Mandatory = $true)][string]$FilePath)

    $file = Get-Item -LiteralPath $FilePath
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $checksumPath = "$($file.FullName).sha256"
    Set-Content -LiteralPath $checksumPath -Value "$hash *$($file.Name)" -Encoding ASCII
    return $checksumPath
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-FullPath (Join-Path $scriptRoot "..")
$publishScript = Join-Path $repoRoot "scripts\publish_portable.ps1"
$installerScript = Join-Path $repoRoot "installer\SerialMonitor.iss"
$releaseRoot = Join-Path $repoRoot "release"
$portableDir = Join-Path $releaseRoot "SerialMonitorPortable"
$portableExe = Join-Path $portableDir "SerialMonitor.WinUI.exe"
$installerOutputDir = Join-Path $releaseRoot "installer"

if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
    throw "Inno Setup script not found: $installerScript"
}

$iscc = Find-InnoSetupCompiler -ExplicitPath $IsccPath
if (-not $iscc) {
    Write-Host "ISCC.exe was not found."
    Write-Host "Install Inno Setup or add ISCC.exe to PATH."
    Write-Host "Download Inno Setup from https://jrsoftware.org/isinfo.php if allowed by company policy."
    exit 1
}

if (-not $SkipPublish) {
    if (-not (Test-Path -LiteralPath $publishScript -PathType Leaf)) {
        throw "Portable publish script not found: $publishScript"
    }

    $publishArgs = @(
        "-ExecutionPolicy", "Bypass",
        "-File", $publishScript,
        "-Configuration", $Configuration
    )

    if ($StopRunning) {
        $publishArgs += "-StopRunning"
    }

    Write-Host "Refreshing portable publish output..."
    & powershell @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Portable publish failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $portableDir -PathType Container)) {
    throw "Portable release folder not found: $portableDir"
}

if (-not (Test-Path -LiteralPath $portableExe -PathType Leaf)) {
    throw "Portable executable not found: $portableExe"
}

New-Item -ItemType Directory -Force -Path $installerOutputDir | Out-Null

$startedAt = Get-Date
Write-Host ""
Write-Host "Compiling Inno Setup installer..."
Write-Host "`"$iscc`" `"$installerScript`""
& $iscc $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
}

$installer = Get-ChildItem -LiteralPath $installerOutputDir -Filter "SerialMonitorSetup_*.exe" -File |
    Where-Object { $_.LastWriteTime -ge $startedAt.AddSeconds(-5) } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $installer) {
    $installer = Get-ChildItem -LiteralPath $installerOutputDir -Filter "SerialMonitorSetup_*.exe" -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

$installerChecksumPath = Write-Sha256File -FilePath $installer.FullName

if (-not $installer) {
    throw "Installer output was not produced in: $installerOutputDir"
}

$packageExtensions = @(".msix", ".msixbundle", ".appx", ".appxbundle")
$packageFiles = @(Get-ChildItem -LiteralPath $releaseRoot -Recurse -File |
    Where-Object { $packageExtensions -contains $_.Extension.ToLowerInvariant() })
if ($packageFiles.Count -gt 0) {
    $packageList = ($packageFiles | Select-Object -ExpandProperty FullName) -join [Environment]::NewLine
    throw "Unexpected package file(s) found. MSIX/AppX output is not allowed:$([Environment]::NewLine)$packageList"
}

Write-Host ""
Write-Host "Installer build complete."
Write-Host "Portable folder: $portableDir"
Write-Host "Installer: $($installer.FullName)"
Write-Host "SHA-256: $installerChecksumPath"
Write-Host "MSIX/AppX output: none"
