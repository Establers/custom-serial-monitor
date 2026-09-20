param(
    [ValidateSet('available', 'current', 'offline', 'checking')]
    [string]$Scenario = 'available'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$previewDirectory = Join-Path $repoRoot 'artifacts\update-preview'
$project = Join-Path $repoRoot 'SerialMonitor.WinUI\SerialMonitor.WinUI.csproj'

# The separate Debug identity avoids replacing a running application.
& dotnet build $project -c Debug -p:Platform=x64 -p:UpdatePreviewBuild=true -o "$previewDirectory\"
if ($LASTEXITCODE -ne 0) { throw 'Update preview build failed. Close any previous preview window before rebuilding.' }

Write-Host 'Open About to inspect updates. Preview data does not change your update preferences.'
# This script explicitly launches the interactive preview requested by its caller.
Start-Process -FilePath (Join-Path $previewDirectory 'SerialMonitor.UpdatePreview.exe') -ArgumentList "--update-preview=$Scenario"
