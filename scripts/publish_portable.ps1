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

    $buildRoot = (Resolve-FullPath $buildDir).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $buildPrefix = $buildRoot + [System.IO.Path]::DirectorySeparatorChar
    $xbfFiles = @(Get-ChildItem -LiteralPath $buildRoot -Filter "*.xbf" -File -Recurse)
    if ($xbfFiles.Count -eq 0) {
        throw "No .xbf files found in WinUI build output: $buildDir"
    }

    foreach ($xbfFile in $xbfFiles) {
        # XAML resource URIs retain their project-relative folders (e.g. Views/).
        $sourcePath = Assert-ChildPath -Path $xbfFile.FullName -ParentPath $buildRoot
        $relativePath = $sourcePath.Substring($buildPrefix.Length)
        $targetPath = Assert-ChildPath -Path (Join-Path $DestinationPath $relativePath) -ParentPath $DestinationPath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targetPath) | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
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

$cliPublishArgs = @(
    "publish", (Join-Path $repoRoot "SerialMonitor.Cli\SerialMonitor.Cli.csproj"),
    "-c", $Configuration, "-r", "win-x64", "--self-contained", "true",
    "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false", "-o", (Join-Path $portableDirFull "tools")
)
$cliExitCode = Invoke-DotnetPublish -PublishArgs $cliPublishArgs -Label "serialctl CLI / MCP"
if ($cliExitCode -ne 0) { throw "serialctl publish failed with exit code $cliExitCode." }

$exePath = Join-Path $portableDirFull $exeName
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Portable executable was not produced: $exePath"
}

Copy-WinUIResourceArtifacts -RepoPath $repoRoot -ConfigurationName $Configuration -DestinationPath $portableDirFull
Copy-Item -LiteralPath $licensePath -Destination (Join-Path $portableDirFull "LICENSE") -Force
# App setup refreshes a local marketplace source only. AI clients remain in
# control of plugin installation, trust and reload; no client settings are edited.
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\agent_plugins.md') -Destination (Join-Path $portableDirFull 'docs\agent_plugins.md') -Force
$pluginPackage = & (Join-Path $scriptRoot 'package_agent_plugin.ps1') -OutputDirectory $releaseRootFull `
    -StageDirectory (Join-Path $portableDirFull 'agent-plugin')

$requiredAssets = @(
    "App.xbf",
    "MainWindow.xbf",
    "Views\PluginGuideView.xbf",
    "SerialMonitor.WinUI.pri",
    "Assets\xterm\index.html",
    "Assets\xterm\xterm.js",
    "Assets\xterm\compact-scrollback.js",
    "Assets\xterm\fonts\JetBrainsMono-Regular.woff2",
    "Assets\xterm\fonts\JetBrainsMono-Bold.woff2",
    "Assets\xterm\fonts\OFL.txt",
    "Assets\xterm\fonts\THIRD_PARTY_NOTICE.txt",
    "Assets\context\index.html",
    "Assets\FunBackgrounds\default_cute_bg.jpg",
    "Assets\AppIcon\SerialMonitor.ico",
    "docs\sequence_authoring.md",
    "docs\sequence_examples.json",
    "docs\ai_control.md",
    "docs\agent_skills.md",
    "docs\agent_plugins.md",
    "agent-plugin\.claude-plugin\marketplace.json",
    "agent-plugin\plugins\serial-monitor\plugin.json",
    "agent-plugin\plugins\serial-monitor\scripts\Start-SerialMonitorMcp.ps1",
    "agent-plugin\plugins\serial-monitor\skills\serial-monitor-control\SKILL.md",
    "tools\serialctl.exe",
    "LICENSE"
)

foreach ($relativeAsset in $requiredAssets) {
    $assetPath = Join-Path $portableDirFull $relativeAsset
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Required asset missing from portable output: $relativeAsset"
    }
}

if (Test-Path -LiteralPath (Join-Path $portableDirFull 'skills')) {
    throw 'Standalone skills must not be installed by app setup. Use the bundled agent-plugin marketplace or the legacy skill ZIP.'
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
- Runtime diagnostics: %LOCALAPPDATA%\SerialMonitor\diagnostics\instance-<pid>-<launch-id>\last_runtime_error.txt
- Each launch creates a separate instance-* folder. Check last_startup.txt in that folder to identify the launch by its startup time.
- last_runtime_error.txt exists only after a runtime error. Root-level diagnostics .txt files are legacy records and are not updated by this version.
- WebView2 Runtime may still be required on the target PC for the xterm log view.
- JetBrains Mono is bundled for the xterm log view under Assets\xterm\fonts.
- Its SIL Open Font License is included at Assets\xterm\fonts\OFL.txt.

AI sequence authoring
---------------------
- Open docs\sequence_authoring.md in a text editor (Korean guide with an AI prompt).
- Attach the guide and your device command specification when asking AI for sequences.
- Use Sequence options > Create sequences with AI for a prompt and a JSON template.
- docs\sequence_examples.json is one importable sequence. Each JSON file contains one sequence.
- Use Sequence options > Load JSON files to select multiple files; Export shares the selected sequence with its _manual.
- Invalid formats or duplicate names cancel the entire import. Limit: 100 files, 10000 total steps, 16 MiB total (2 MiB/file).
- Response waits and branching are not supported. TX OK does not verify device receipt or execution.

AI device control
-----------------
- Open docs\agent_plugins.md for the Codex / Claude Code / Copilot plugin.
- The agent-plugin folder is a local marketplace source; register and install it once using the app Plugin tab.
- App upgrades refresh this bundled source. Reload/update the plugin in the AI host as directed by the guide.
- SerialMonitorPlugin.zip is the standalone plugin marketplace download; Git updates use Establers/custom-serial-monitor.
- App setup never installs plugins into AI clients or changes their permissions/settings.
- SerialMonitorSkills.zip remains available only for legacy standalone skill installation.
- tools\serialctl.exe controls this running GUI and collects actual RX; the GUI owns the COM port.
- The plugin supplies the shared skill and local MCP launcher. Use the app Plugin tab for setup and updates.
- CLI fallback and advanced manual MCP setup remain documented in docs\ai_control.md.

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
    $downloadZipPath = Join-Path $releaseRootFull 'SerialMonitorPortable.zip'
    Copy-Item -LiteralPath $zipPath -Destination $downloadZipPath -Force
    $downloadZipChecksumPath = Write-Sha256File -FilePath $downloadZipPath
    $skillPackage = & (Join-Path $scriptRoot 'package_agent_skill.ps1') -OutputDirectory $releaseRootFull
}

$fileCount = @(Get-ChildItem -LiteralPath $portableDirFull -Recurse -File).Count
Write-Host ""
Write-Host "Portable publish complete."
Write-Host "Folder: $portableDirFull"
Write-Host "Executable: $exePath"
Write-Host "Files: $fileCount"
Write-Host "Plugin marketplace: $($pluginPackage.StageDirectory)"
Write-Host "Plugin download: $($pluginPackage.ZipPath)"
Write-Host "Plugin SHA-256: $($pluginPackage.ChecksumPath)"
if ($zipPath) {
    Write-Host "Zip: $zipPath"
    Write-Host "SHA-256: $zipChecksumPath"
    Write-Host "Portable download: $downloadZipPath"
    Write-Host "Portable download SHA-256: $downloadZipChecksumPath"
    Write-Host "Standalone skills: $($skillPackage.ZipPath)"
    Write-Host "Skills SHA-256: $($skillPackage.ChecksumPath)"
}
Write-Host "MSIX/AppX output: none"
