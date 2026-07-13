[CmdletBinding()]
param(
    [switch]$StopRunning,
    [switch]$NoZip,
    [string]$Configuration = "Release"
)

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
    $parentPrefix = $fullParent.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate on path outside release folder: $fullPath"
    }

    return $fullPath
}

function Remove-PortableOutput {
    param(
        [Parameter(Mandatory = $true)][string]$PortablePath,
        [Parameter(Mandatory = $true)][string]$ReleasePath
    )

    $safePortablePath = Assert-ChildPath -Path $PortablePath -ParentPath $ReleasePath
    if (Test-Path -LiteralPath $safePortablePath) {
        Remove-Item -LiteralPath $safePortablePath -Recurse -Force
    }
}

function Invoke-DotnetPublish {
    param(
        [Parameter(Mandatory = $true)][string[]]$PublishArgs,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Write-Host ""
    Write-Host "Running $Label publish..."
    Write-Host "dotnet $($PublishArgs -join ' ')"
    & dotnet @PublishArgs 2>&1 | ForEach-Object { Write-Host $_ }
    return [int]$LASTEXITCODE
}

function Write-Sha256File {
    param([Parameter(Mandatory = $true)][string]$FilePath)

    $file = Get-Item -LiteralPath $FilePath
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $checksumPath = "$($file.FullName).sha256"
    Set-Content -LiteralPath $checksumPath -Value "$hash *$($file.Name)" -Encoding ASCII
    return $checksumPath
}

function Copy-WinUIResourceArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$RepoPath,
        [Parameter(Mandatory = $true)][string]$ConfigurationName,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $candidateBuildDirs = @(
        (Join-Path $RepoPath "SerialMonitor.WinUI\bin\x64\$ConfigurationName\net8.0-windows10.0.19041.0\win-x64"),
        (Join-Path $RepoPath "SerialMonitor.WinUI\bin\x64\$ConfigurationName\net8.0-windows10.0.19041.0\win10-x64")
    )

    $buildDir = $candidateBuildDirs | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $buildDir) {
        throw "Could not find WinUI build output folder for XAML resources."
    }

    $xbfFiles = @(Get-ChildItem -LiteralPath $buildDir -Filter "*.xbf" -File -ErrorAction SilentlyContinue)
    if ($xbfFiles.Count -eq 0) {
        throw "No .xbf files found in WinUI build output: $buildDir"
    }

    foreach ($xbfFile in $xbfFiles) {
        Copy-Item -LiteralPath $xbfFile.FullName -Destination (Join-Path $DestinationPath $xbfFile.Name) -Force
    }

    $appPri = Join-Path $buildDir "SerialMonitor.WinUI.pri"
    if (-not (Test-Path -LiteralPath $appPri)) {
        throw "App PRI file missing from WinUI build output: $appPri"
    }

    Copy-Item -LiteralPath $appPri -Destination (Join-Path $DestinationPath "SerialMonitor.WinUI.pri") -Force
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-FullPath (Join-Path $scriptRoot "..")
$projectPath = Join-Path $repoRoot "SerialMonitor.WinUI\SerialMonitor.WinUI.csproj"
$licensePath = Join-Path $repoRoot "LICENSE"
$releaseRoot = Join-Path $repoRoot "release"
$portableDir = Join-Path $releaseRoot "SerialMonitorPortable"
$appProcessName = "SerialMonitor.WinUI"
$exeName = "SerialMonitor.WinUI.exe"

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file not found: $projectPath"
}

if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
    throw "License file not found: $licensePath"
}

$releaseRootFull = Resolve-FullPath $releaseRoot
$portableDirFull = Assert-ChildPath -Path $portableDir -ParentPath $releaseRootFull

$runningProcesses = @(Get-Process -Name $appProcessName -ErrorAction SilentlyContinue)
if ($runningProcesses.Count -gt 0) {
    $processList = ($runningProcesses | ForEach-Object { "$($_.ProcessName) PID=$($_.Id)" }) -join ", "
    if ($StopRunning) {
        Write-Warning "Stopping running Serial Monitor process(es): $processList"
        $runningProcesses | Stop-Process -Force
        foreach ($process in $runningProcesses) {
            try {
                Wait-Process -Id $process.Id -Timeout 10 -ErrorAction Stop
            }
            catch {
                Write-Warning "Process PID=$($process.Id) did not exit within 10 seconds."
            }
        }
    }
    else {
        Write-Warning "Serial Monitor is currently running: $processList"
        Write-Warning "Close it first if publish fails due locked files, or rerun this script with -StopRunning after saving app state."
    }
}

New-Item -ItemType Directory -Force -Path $releaseRootFull | Out-Null

Remove-PortableOutput -PortablePath $portableDirFull -ReleasePath $releaseRootFull

Get-ChildItem -LiteralPath $releaseRootFull -Filter "SerialMonitorPortable_*.zip" -File -ErrorAction SilentlyContinue |
    ForEach-Object {
        $safeZipPath = Assert-ChildPath -Path $_.FullName -ParentPath $releaseRootFull
        Remove-Item -LiteralPath $safeZipPath -Force
    }

$commonPublishArgs = @(
    "publish",
    $projectPath,
    "-c", $Configuration,
    "-p:Platform=x64",
    "-p:WindowsPackageType=None",
    "-p:SelfContained=true",
    "-p:WindowsAppSDKSelfContained=true",
    "-p:PublishSingleFile=false",
    "-p:PublishTrimmed=false",
    "-o", $portableDirFull
)

$primaryPublishArgs = $commonPublishArgs + @("-p:RuntimeIdentifier=win-x64")
$exitCode = Invoke-DotnetPublish -PublishArgs $primaryPublishArgs -Label "portable win-x64"

if ($exitCode -ne 0) {
    Write-Warning "Primary publish failed with exit code $exitCode. Retrying with RuntimeIdentifierOverride=win-x64."
    Remove-PortableOutput -PortablePath $portableDirFull -ReleasePath $releaseRootFull
    $fallbackPublishArgs = $commonPublishArgs + @("-p:RuntimeIdentifierOverride=win-x64")
    $exitCode = Invoke-DotnetPublish -PublishArgs $fallbackPublishArgs -Label "portable RuntimeIdentifierOverride"
}

if ($exitCode -ne 0) {
    throw "dotnet publish failed with exit code $exitCode."
}

$exePath = Join-Path $portableDirFull $exeName
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Portable executable was not produced: $exePath"
}

Copy-WinUIResourceArtifacts -RepoPath $repoRoot -ConfigurationName $Configuration -DestinationPath $portableDirFull
Copy-Item -LiteralPath $licensePath -Destination (Join-Path $portableDirFull "LICENSE") -Force

$requiredAssets = @(
    "App.xbf",
    "MainWindow.xbf",
    "SerialMonitor.WinUI.pri",
    "Assets\xterm\index.html",
    "Assets\xterm\xterm.js",
    "Assets\context\index.html",
    "Assets\FunBackgrounds\default_cute_bg.jpg",
    "Assets\AppIcon\SerialMonitor.ico",
    "LICENSE"
)

foreach ($relativeAsset in $requiredAssets) {
    $assetPath = Join-Path $portableDirFull $relativeAsset
    if (-not (Test-Path -LiteralPath $assetPath)) {
        throw "Required asset missing from portable output: $relativeAsset"
    }
}

$readmePath = Join-Path $portableDirFull "README_PORTABLE.txt"
$readmeText = @"
Serial Monitor Portable
=======================

This is a no-install portable build of Serial Monitor.

How to run
----------
1. Unzip the full SerialMonitorPortable folder.
2. Run SerialMonitor.WinUI.exe.
3. Keep all files in this folder together.
4. Do not run the app from inside the zip file.

Notes
-----
- This build is unpackaged and intentionally does not use MSIX.
- No installer and no admin rights are required.
- MSIX is intentionally not used because target company PCs may block MSIX installation.
- Logs and profiles are still stored under %LOCALAPPDATA%\SerialMonitor.
- Default logs: %LOCALAPPDATA%\SerialMonitor\logs
- Default profile: %LOCALAPPDATA%\SerialMonitor\profiles\default.json
- Runtime diagnostics: %LOCALAPPDATA%\SerialMonitor\diagnostics\last_runtime_error.txt
- WebView2 Runtime may still be required on the target PC for the xterm log view.

If the app does not start
-------------------------
- Check the Windows version.
- Install or repair Microsoft Edge WebView2 Runtime if allowed by IT policy.
- Check whether company security blocked the exe or dll files.
- Confirm the folder was copied completely and files were not removed by antivirus.
- Do not move individual dlls or Assets folders away from SerialMonitor.WinUI.exe.
"@
Set-Content -LiteralPath $readmePath -Value $readmeText -Encoding UTF8

$packageExtensions = @(".msix", ".msixbundle", ".appx", ".appxbundle")
$packageFiles = @(Get-ChildItem -LiteralPath $releaseRootFull -Recurse -File |
    Where-Object { $packageExtensions -contains $_.Extension.ToLowerInvariant() })
if ($packageFiles.Count -gt 0) {
    $packageList = ($packageFiles | Select-Object -ExpandProperty FullName) -join [Environment]::NewLine
    throw "Unexpected package file(s) found. MSIX/AppX output is not allowed:$([Environment]::NewLine)$packageList"
}

$sourceExtensions = @(".cs", ".xaml", ".csproj", ".sln")
$sourceFiles = @(Get-ChildItem -LiteralPath $portableDirFull -Recurse -File |
    Where-Object { $sourceExtensions -contains $_.Extension.ToLowerInvariant() })
if ($sourceFiles.Count -gt 0) {
    $sourceList = ($sourceFiles | Select-Object -ExpandProperty FullName) -join [Environment]::NewLine
    throw "Unexpected source/project file(s) found in portable output:$([Environment]::NewLine)$sourceList"
}

$zipPath = $null
$zipChecksumPath = $null
if (-not $NoZip) {
    $timestamp = Get-Date -Format "yyyyMMdd_HHmm"
    $zipPath = Join-Path $releaseRootFull "SerialMonitorPortable_$timestamp.zip"
    Compress-Archive -LiteralPath $portableDirFull -DestinationPath $zipPath -Force
    $zipChecksumPath = Write-Sha256File -FilePath $zipPath
}

$fileCount = @(Get-ChildItem -LiteralPath $portableDirFull -Recurse -File).Count
Write-Host ""
Write-Host "Portable publish complete."
Write-Host "Folder: $portableDirFull"
Write-Host "Executable: $exePath"
Write-Host "Files: $fileCount"
if ($zipPath) {
    Write-Host "Zip: $zipPath"
    Write-Host "SHA-256: $zipChecksumPath"
}
Write-Host "MSIX/AppX output: none"
