[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts/update-site",
    [string]$Repository = "Establers/custom-serial-monitor"
)
$ErrorActionPreference = "Stop"

# Query the currently published latest release, not the event tag or main's version.
# This also avoids advertising a prerelease or an older release edited later.
$response = & gh api "repos/$Repository/releases/latest"
if ($LASTEXITCODE -ne 0) { throw "Could not read latest published release." }
$release = $response | ConvertFrom-Json
if ($release.draft -or $release.prerelease -or -not $release.published_at -or
    $release.tag_name -notmatch '^v?\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Latest release is not a supported published stable version."
}
$manifest = [ordered]@{
    tag_name = $release.tag_name
    draft = $false
    prerelease = $false
    published_at = $release.published_at
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'updates.json') -Encoding utf8
'<!doctype html><meta charset="utf-8"><title>Serial Monitor updates</title><a href="updates.json">Update manifest</a>' |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'index.html') -Encoding utf8

