[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PortableDirectory)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$payloadRoot = [IO.Path]::GetFullPath($PortableDirectory)

function Read-Json([string]$Path) { return [IO.File]::ReadAllText($Path) | ConvertFrom-Json }
function Get-ProjectVersion([string]$Relative) {
    $project = [xml][IO.File]::ReadAllText((Join-Path $repoRoot $Relative))
    return [version]([string]$project.Project.PropertyGroup.Version | Where-Object { $_ })
}
function Assert-SameFile([string]$Relative, [string]$PayloadRelative = '') {
    $source = Join-Path $repoRoot $Relative
    if (-not $PayloadRelative) {
        $PayloadRelative = $Relative
        if ($Relative.StartsWith('plugins\') -or $Relative.StartsWith('.agents\') -or $Relative.StartsWith('.claude-plugin\')) {
            $PayloadRelative = 'agent-plugin\' + $Relative
        }
    }
    $destination = Join-Path $payloadRoot $PayloadRelative
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
        throw "Release payload is missing or stale: $Relative. Rebuild the portable package before creating the installer."
    }
}

$appVersion = Get-ProjectVersion 'SerialMonitor.WinUI\SerialMonitor.WinUI.csproj'
$cliVersion = Get-ProjectVersion 'SerialMonitor.Cli\SerialMonitor.Cli.csproj'
$installerText = [IO.File]::ReadAllText((Join-Path $repoRoot 'installer\SerialMonitor.iss'))
$installerVersion = [regex]::Match($installerText, '(?m)^#define MyAppVersion "([^"]+)"').Groups[1].Value
if ($appVersion -ne $cliVersion -or $appVersion -ne [version]$installerVersion) {
    throw 'GUI, CLI and installer source versions must match.'
}

# Check the browser entry point and background module even with -SkipPublish.
# A missing module can stop terminal initialization despite current EXE versions.
$requiredUiAssets = @('Assets\xterm\index.html', 'Assets\xterm\terminal-background.js',
    'Assets\FunBackgrounds\default_cute_bg.jpg')
foreach ($relative in $requiredUiAssets) {
    Assert-SameFile ('SerialMonitor.WinUI\' + $relative) $relative
}
$terminalAssetRoot = Join-Path $repoRoot 'SerialMonitor.WinUI\Assets\xterm'
foreach ($file in @(Get-ChildItem -LiteralPath $terminalAssetRoot -Recurse -File)) {
    $relative = 'Assets\xterm\' + $file.FullName.Substring($terminalAssetRoot.TrimEnd('\', '/').Length + 1)
    if ($relative -notin $requiredUiAssets) { Assert-SameFile ('SerialMonitor.WinUI\' + $relative) $relative }
}

foreach ($relative in @('SerialMonitor.WinUI.exe', 'tools\serialctl.exe')) {
    $executable = Join-Path $payloadRoot $relative
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Release executable is missing: $relative" }
    $actual = [version](([Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion -split '[-+]')[0])
    if ($actual -ne $appVersion) { throw "Stale release executable: $relative is $actual; expected $appVersion." }
}

# Version numbers alone do not detect an unchanged manifest with outdated skills
# or launch scripts. Compare the full managed plugin source before probing it.
$null = & (Join-Path $PSScriptRoot 'package_agent_plugin.ps1') -Check
foreach ($relative in @('.agents\plugins\marketplace.json', '.claude-plugin\marketplace.json',
    'docs\agent_plugins.md', 'docs\ai_control.md')) { Assert-SameFile $relative }
foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'plugins\serial-monitor') -Recurse -Force -File)) {
    if ($file.Name -in @('serial-monitor.local.json', '.serial-monitor-skill.json')) { continue }
    Assert-SameFile $file.FullName.Substring($repoRoot.TrimEnd('\', '/').Length + 1)
}

$launcher = Join-Path $payloadRoot 'agent-plugin\plugins\serial-monitor\scripts\Start-SerialMonitorMcp.ps1'
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$description = & $powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $launcher `
    -CliPath (Join-Path $payloadRoot 'tools\serialctl.exe') -Mode describe
if ($LASTEXITCODE -ne 0) { throw 'The bundled plugin cannot negotiate with the bundled CLI.' }
$metadata = ($description -join "`n") | ConvertFrom-Json
if (-not $metadata.success -or [version](($metadata.data.cliVersion -split '[-+]')[0]) -ne $cliVersion) {
    throw 'Bundled CLI discovery does not match the release version.'
}
$plugin = Read-Json (Join-Path $payloadRoot 'agent-plugin\plugins\serial-monitor\plugin.json')
[PSCustomObject]@{ Version = $appVersion.ToString(); PluginVersion = $plugin.version; PortableDirectory = $payloadRoot; Checked = $true }
