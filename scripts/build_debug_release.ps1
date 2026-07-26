[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ParentPath
    )

    $fullPath = Resolve-FullPath $Path
    $fullParent = Resolve-FullPath $ParentPath
    $parentPrefix = $fullParent.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate on a path outside the build output folder: $fullPath"
    }

    return $fullPath
}

function Invoke-AppBuild {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    # WinUI's XAML compiler concatenates some output paths, so keep the trailing separator.
    $outputWithSeparator = $OutputPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar

    $buildArgs = @(
        "build",
        $ProjectPath,
        "--configuration", $Configuration,
        "--property:Platform=x64",
        "--property:WindowsPackageType=None",
        "--property:GenerateAppxPackageOnBuild=false",
        "--property:AppxPackageSigningEnabled=false",
        "--output", $outputWithSeparator
    )

    Write-Host ""
    Write-Host "Building $Configuration..."
    & dotnet @buildArgs

    if ($LASTEXITCODE -ne 0) {
        throw "$Configuration build failed with exit code $LASTEXITCODE."
    }

    $executablePath = Join-Path $OutputPath "SerialMonitor.WinUI.exe"
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "$Configuration executable was not produced: $executablePath"
    }

    Write-Host "$Configuration output: $OutputPath"
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-FullPath (Join-Path $scriptRoot "..")
$projectPath = Join-Path $repoRoot "SerialMonitor.WinUI\SerialMonitor.WinUI.csproj"
$artifactsRoot = Join-Path $repoRoot "artifacts"
$buildRoot = Join-Path $artifactsRoot "build"

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Project file not found: $projectPath"
}

$artifactsRootFull = Resolve-FullPath $artifactsRoot
$buildRootFull = Assert-ChildPath -Path $buildRoot -ParentPath $artifactsRootFull

New-Item -ItemType Directory -Force -Path $buildRootFull | Out-Null

foreach ($configuration in @("Debug", "Release")) {
    $outputPath = Assert-ChildPath `
        -Path (Join-Path $buildRootFull $configuration) `
        -ParentPath $buildRootFull

    if (Test-Path -LiteralPath $outputPath) {
        Remove-Item -LiteralPath $outputPath -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
    Invoke-AppBuild `
        -ProjectPath $projectPath `
        -Configuration $configuration `
        -OutputPath $outputPath
}

Write-Host ""
Write-Host "Debug and Release builds completed."
Write-Host "Debug : $(Join-Path $buildRootFull 'Debug')"
Write-Host "Release: $(Join-Path $buildRootFull 'Release')"
Write-Host "Tests, ZIP files, installers, and MSIX/AppX packages were not created."
