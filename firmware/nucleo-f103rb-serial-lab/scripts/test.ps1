[CmdletBinding()]
param([ValidatePattern('^build(?:-[A-Za-z0-9_-]+)?$')][string]$BuildDirectory = 'build')
$ErrorActionPreference = 'Stop'
$labRoot = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $labRoot $BuildDirectory
$null = New-Item -ItemType Directory -Path $buildDir -Force
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio C++ build tools for native protocol tests (firmware build does not need them).' }
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw 'Visual Studio C++ build tools not found.' }
$vcvars = Join-Path $vsRoot 'VC\Auxiliary\Build\vcvars64.bat'
$exe = Join-Path $buildDir 'test-lab.exe'
$buildFlags = ''
if (Test-Path -LiteralPath (Join-Path $buildDir 'lab_build_info.h')) { $buildFlags = "/DLAB_TARGET_BUILD /I`"$buildDir`"" }
# Use a batch file so vcvars affects cl; all paths are locally resolved, not device input.
$batch = @"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
cl.exe /nologo /W4 /WX /std:c11 /D_CRT_SECURE_NO_WARNINGS $buildFlags /I"$labRoot\include" "$labRoot\src\lab.c" "$labRoot\src\lab_fixtures.c" "$labRoot\tests\test_lab.c" /Fe:"$exe" /Fo:"$buildDir\\"
if errorlevel 1 exit /b 1
"$exe" --export "$buildDir"
exit /b %errorlevel%
"@
$batchPath = Join-Path $buildDir 'test-native.cmd'
[IO.File]::WriteAllText($batchPath, $batch, [Text.Encoding]::Default)
& $env:ComSpec /d /c $batchPath
if ($LASTEXITCODE -ne 0) { throw "Native protocol tests failed ($LASTEXITCODE)." }
