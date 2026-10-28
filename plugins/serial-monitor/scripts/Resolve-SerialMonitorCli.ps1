[CmdletBinding()]
param([string]$CliPath)

$ErrorActionPreference = 'Stop'
# Reuse the canonical resolver so CLI fallback and MCP startup select the same app.
$resolver = Join-Path (Split-Path -Parent $PSScriptRoot) 'skills\serial-monitor-control\scripts\Find-SerialCtl.ps1'
& $resolver -CliPath $CliPath