[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$StageDirectory,
    [switch]$SyncOnly,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$pluginRoot = Join-Path $repoRoot 'plugins\serial-monitor'
$skillSource = Join-Path $repoRoot 'skills\serial-monitor-control'
$skillTarget = Join-Path $pluginRoot 'skills\serial-monitor-control'
$marketplaceSource = Join-Path $repoRoot '.claude-plugin\marketplace.json'
$excludedNames = @('serial-monitor.local.json', '.serial-monitor-skill.json')
$binaryExtensions = @('.exe', '.dll', '.pdb', '.bin')
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-ChildPath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package path escapes its intended root: $full"
    }
    return $full
}

function Get-PackageFiles([string]$Root) {
    $entries = @(Get-ChildItem -LiteralPath $Root -Recurse -Force)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw "Plugin packages must not contain symbolic links: $Root"
    }
    $files = @($entries | Where-Object { -not $_.PSIsContainer -and $_.Name -notin $excludedNames })
    foreach ($file in $files) {
        if ($file.Extension.ToLowerInvariant() -in $binaryExtensions) {
            throw "The plugin must not contain application binaries: $($file.FullName)"
        }
    }
    return $files
}

function Test-SameFile([string]$Left, [string]$Right) {
    return (Test-Path -LiteralPath $Right -PathType Leaf) -and
        (Get-FileHash -LiteralPath $Left -Algorithm SHA256).Hash -eq
        (Get-FileHash -LiteralPath $Right -Algorithm SHA256).Hash
}

$sourceFiles = @(Get-PackageFiles $skillSource)
$sourcePrefix = $skillSource.TrimEnd('\', '/') + '\'
$expectedSkillFiles = @{}
foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($sourcePrefix.Length)
    $expectedSkillFiles[$relative] = $file.FullName
}
if (-not $expectedSkillFiles.ContainsKey('SKILL.md')) { throw 'The canonical skill is missing SKILL.md.' }

# Only this generated, repository-owned skill subtree is synchronized. Never touch
# a user's discovered skill directories or the AI client's installed plugin cache.
if (Test-Path -LiteralPath $skillTarget) {
    $targetRootItem = Get-Item -LiteralPath $skillTarget -Force
    if ($targetRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Generated skill root must not be a symbolic link.' }
    foreach ($file in @(Get-PackageFiles $skillTarget)) {
        $relative = $file.FullName.Substring($skillTarget.TrimEnd('\', '/').Length + 1)
        if (-not $expectedSkillFiles.ContainsKey($relative)) {
            if ($Check) { throw "Generated plugin skill has an unexpected file: $relative" }
            $safeFile = Assert-ChildPath $file.FullName $skillTarget
            Remove-Item -LiteralPath $safeFile -Force
        }
    }
}
foreach ($relative in $expectedSkillFiles.Keys) {
    $destination = Assert-ChildPath (Join-Path $skillTarget $relative) $pluginRoot
    if (-not (Test-SameFile $expectedSkillFiles[$relative] $destination)) {
        if ($Check) { throw "Generated plugin skill is out of date: $relative. Run scripts\package_agent_plugin.ps1 -SyncOnly." }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $expectedSkillFiles[$relative] -Destination $destination -Force
    }
}
$licenseDestination = Join-Path $pluginRoot 'LICENSE'
if (-not (Test-SameFile (Join-Path $repoRoot 'LICENSE') $licenseDestination)) {
    if ($Check) { throw 'The plugin LICENSE is out of date.' }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $licenseDestination -Force
}

$manifest = [IO.File]::ReadAllText((Join-Path $pluginRoot 'plugin.json')) | ConvertFrom-Json
$marketplace = [IO.File]::ReadAllText($marketplaceSource) | ConvertFrom-Json
$compatibility = [IO.File]::ReadAllText((Join-Path $pluginRoot 'compatibility.json')) | ConvertFrom-Json
if ($manifest.name -ne 'serial-monitor' -or $marketplace.name -ne 'serial-monitor-tools') { throw 'Unexpected plugin or marketplace identity.' }
if ($manifest.version -ne $compatibility.pluginVersion -or $manifest.version -ne $marketplace.plugins[0].version) {
    throw 'Plugin, marketplace and compatibility versions must match.'
}
foreach ($relative in @('.codex-plugin\plugin.json', '.claude-plugin\plugin.json')) {
    $overlay = [IO.File]::ReadAllText((Join-Path $pluginRoot $relative)) | ConvertFrom-Json
    if ($overlay.name -ne $manifest.name -or $overlay.version -ne $manifest.version) { throw "Plugin overlay identity/version does not match: $relative" }
}
$pluginFiles = @(Get-PackageFiles $pluginRoot)
if ($Check -or $SyncOnly) {
    return [PSCustomObject]@{ PluginRoot = $pluginRoot; Version = $manifest.version; Files = $pluginFiles.Count; Checked = [bool]$Check }
}

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'release' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
if (-not $StageDirectory) { $StageDirectory = Join-Path $outputRoot 'SerialMonitorPlugin' }
$stageRoot = [IO.Path]::GetFullPath($StageDirectory)
if ($stageRoot -eq $repoRoot -or $stageRoot -eq $pluginRoot -or $stageRoot -eq $outputRoot) {
    throw 'The package stage must be a dedicated output directory.'
}
$stageMarker = Join-Path $stageRoot '.serial-monitor-plugin-package.json'
if (Test-Path -LiteralPath $stageRoot) {
    $stageItem = Get-Item -LiteralPath $stageRoot -Force
    if ($stageItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The package stage must not be a symbolic link.' }
    if (@(Get-ChildItem -LiteralPath $stageRoot -Force).Count -gt 0 -and -not (Test-Path -LiteralPath $stageMarker -PathType Leaf)) {
        throw "Refusing to replace an unmanaged nonempty package directory: $stageRoot"
    }
    # Clear only individual files listed beneath this owned package root; verify
    # reparse points and absolute containment before any removal.
    foreach ($file in @(Get-PackageFiles $stageRoot)) {
        $safeFile = Assert-ChildPath $file.FullName $stageRoot
        Remove-Item -LiteralPath $safeFile -Force
    }
}
New-Item -ItemType Directory -Force -Path (Join-Path $stageRoot '.claude-plugin') | Out-Null
Copy-Item -LiteralPath $marketplaceSource -Destination (Join-Path $stageRoot '.claude-plugin\marketplace.json') -Force
foreach ($file in $pluginFiles) {
    $relative = $file.FullName.Substring($pluginRoot.TrimEnd('\', '/').Length + 1)
    $destination = Assert-ChildPath (Join-Path $stageRoot ('plugins\serial-monitor\' + $relative)) $stageRoot
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $stageRoot 'docs') | Out-Null
foreach ($relative in @('docs\agent_plugins.md', 'docs\ai_control.md', 'LICENSE')) {
    $source = Join-Path $repoRoot $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Plugin documentation is missing: $relative" }
    if ([IO.Path]::GetExtension($relative) -eq '.md') {
        # Legacy/developer guides stay in the repository rather than recursively
        # pulling unrelated documentation and installer scripts into this package.
        $text = [IO.File]::ReadAllText($source, [Text.Encoding]::UTF8)
        foreach ($guide in @('agent_skills.md', 'manual_test_checklist.md')) {
            $text = $text.Replace(('(' + $guide + ')'),
                ('(https://github.com/Establers/custom-serial-monitor/blob/main/docs/' + $guide + ')'))
        }
        [IO.File]::WriteAllText((Join-Path $stageRoot $relative), $text, $utf8)
    }
    else { Copy-Item -LiteralPath $source -Destination (Join-Path $stageRoot $relative) -Force }
}
$readme = @'
# Serial Monitor Plugin

This directory is a local plugin marketplace for Codex, Claude Code and GitHub
Copilot. Install the Windows Serial Monitor application separately (1.6.0+).

Read docs/agent_plugins.md for Git installation, local installation, updates,
custom executable paths, and migration from standalone skill/MCP registrations.

Local marketplace name: serial-monitor-tools
Plugin name: serial-monitor
Marketplace root: this directory (the parent of .claude-plugin and plugins).

The application installer carries the same source under its agent-plugin folder.
No plugin is automatically installed or registered in an AI client by app setup.
No program binaries, device commands, credentials or machine-specific settings
are included in this plugin package.
'@
[IO.File]::WriteAllText((Join-Path $stageRoot 'README.md'), $readme, $utf8)
[IO.File]::WriteAllText($stageMarker, (@{ name = 'serial-monitor-tools'; version = $manifest.version } | ConvertTo-Json), $utf8)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $outputRoot 'SerialMonitorPlugin.zip'
$temporaryZip = Join-Path $outputRoot ('SerialMonitorPlugin-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $archive = [IO.Compression.ZipFile]::Open($temporaryZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in @(Get-PackageFiles $stageRoot)) {
            $relative = $file.FullName.Substring($stageRoot.TrimEnd('\', '/').Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName,
                ('SerialMonitorPlugin/' + $relative), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
    Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryZip -PathType Leaf) { Remove-Item -LiteralPath $temporaryZip -Force }
}
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$checksumPath = $zipPath + '.sha256'
Set-Content -LiteralPath $checksumPath -Value "$hash *SerialMonitorPlugin.zip" -Encoding ASCII
[PSCustomObject]@{ ZipPath = $zipPath; ChecksumPath = $checksumPath; StageDirectory = $stageRoot; Version = $manifest.version; Files = @(Get-PackageFiles $stageRoot).Count }
