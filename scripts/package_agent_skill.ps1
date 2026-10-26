[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'release' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$skillRoot = Join-Path $repo 'skills\serial-monitor-control'
$skillEntries = @(Get-ChildItem -LiteralPath $skillRoot -Recurse -Force)
if (@($skillEntries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
    throw 'Skill source must not contain symbolic links.'
}

# Package source files only. Machine-specific settings are created by the receiver's installer.
$files = @('LICENSE', 'docs\agent_skills.md', 'docs\ai_control.md', 'skills\Install-Skill.ps1',
    'skills\SkillInstall.Common.ps1')
$files += @($skillEntries | Where-Object {
    -not $_.PSIsContainer -and $_.Name -notin @('serial-monitor.local.json', '.serial-monitor-skill.json')
} | ForEach-Object { $_.FullName.Substring($repo.TrimEnd('\', '/').Length + 1) })
foreach ($relative in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo $relative) -PathType Leaf)) {
        throw "Package source missing: $relative"
    }
    if ([IO.Path]::GetExtension($relative) -in @('.exe', '.dll', '.pdb', '.bin')) {
        throw "The skill package must not include application binaries: $relative"
    }
}
if ($files -notcontains 'skills\serial-monitor-control\SKILL.md') { throw 'SKILL.md is missing.' }

# Keep the ZIP's first page in sync with the maintained Korean guide.
$readme = [IO.File]::ReadAllText((Join-Path $repo 'docs\agent_skills.md'), [Text.Encoding]::UTF8)
$readme = $readme.Replace('(../skills/', '(skills/').Replace('(ai_control.md', '(docs/ai_control.md')

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$zipPath = Join-Path $outputRoot 'SerialMonitorSkills.zip'
if (Test-Path -LiteralPath $zipPath -PathType Container) { throw "Expected a file: $zipPath" }
$tempPath = Join-Path $outputRoot ('SerialMonitorSkills-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $archive = [IO.Compression.ZipFile]::Open($tempPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in $files) {
            $entry = 'SerialMonitorSkills/' + $relative.Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $repo $relative),
                $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        $readmeEntry = $archive.CreateEntry('SerialMonitorSkills/README.md')
        $writer = [IO.StreamWriter]::new($readmeEntry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write($readme) } finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
    Move-Item -LiteralPath $tempPath -Destination $zipPath -Force
}
finally {
    if (Test-Path -LiteralPath $tempPath -PathType Leaf) { Remove-Item -LiteralPath $tempPath -Force }
}
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$checksumPath = $zipPath + '.sha256'
Set-Content -LiteralPath $checksumPath -Value "$hash *SerialMonitorSkills.zip" -Encoding ASCII
[PSCustomObject]@{ ZipPath = $zipPath; ChecksumPath = $checksumPath; Files = $files.Count + 1 }
