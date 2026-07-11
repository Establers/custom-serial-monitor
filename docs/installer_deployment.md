# Inno Setup Installer Deployment

Serial Monitor can be distributed as a normal `.exe` installer in addition to
the portable zip. This installer is optional and does not use MSIX.

## What This Installer Is

- Normal Inno Setup `.exe` installer.
- Installs the already-published portable app files.
- Per-user install by default, so admin rights are not required.
- Creates a Start Menu shortcut named `Serial Monitor`.
- Offers an optional desktop shortcut.
- Keeps the portable zip deployment as the recommended fallback for quick tests.

MSIX is intentionally not used because target company PCs may block MSIX
installation.

## Build Steps

From the repository root:

```powershell
dotnet build SerialMonitor.WinUI\SerialMonitor.WinUI.csproj -p:Platform=x64
powershell -ExecutionPolicy Bypass -File scripts\publish_portable.ps1
powershell -ExecutionPolicy Bypass -File scripts\build_installer.ps1
```

The installer script refreshes the portable publish output by default. If you
already published the portable folder and only want to compile the installer:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_installer.ps1 -SkipPublish
```

## Required Tool

`scripts\build_installer.ps1` requires the Inno Setup compiler:

- `ISCC.exe` in `PATH`, or
- Inno Setup installed in the default `Program Files` location, or
- pass an explicit compiler path with `-IsccPath`.

If `ISCC.exe` is not found, the script prints:

```text
Install Inno Setup or add ISCC.exe to PATH.
```

## Outputs

Portable output remains:

- `release\SerialMonitorPortable`
- `release\SerialMonitorPortable_yyyyMMdd_HHmm.zip`

Installer output:

- `release\installer\SerialMonitorSetup_yyyyMMdd_HHmm.exe`

No `.msix`, `.msixbundle`, `.appx`, or `.appxbundle` package should be produced.

## Install Location

The installer uses a per-user path:

```text
%LOCALAPPDATA%\Programs\SerialMonitor
```

This avoids admin rights by default and keeps company-PC deployment simpler.

## User Data

The installer does not change the app's user data locations:

- Logs: `%LOCALAPPDATA%\SerialMonitor\logs`
- Default profile: `%LOCALAPPDATA%\SerialMonitor\profiles\default.json`
- Runtime diagnostics:
  `%LOCALAPPDATA%\SerialMonitor\diagnostics\last_runtime_error.txt`

Uninstall removes installed program files only. It must not delete logs,
profiles, diagnostics, or other user data under `%LOCALAPPDATA%\SerialMonitor`.

## Target PC Notes

WebView2 Runtime may still be required for the xterm log view and event context
viewer. Many Windows 10/11 PCs already have it through Microsoft Edge, but
company images vary.

If the app or installer does not run, check:

- Windows version
- Microsoft Edge WebView2 Runtime
- company security blocking the installer `.exe`
- company security blocking installed `.exe` or `.dll` files
- incomplete portable folder output before building the installer

If company security blocks the installer executable, use the portable zip
instead.

## Verification Checklist

- [ ] `release\SerialMonitorPortable` exists.
- [ ] `release\SerialMonitorPortable\SerialMonitor.WinUI.exe` exists.
- [ ] `release\installer\SerialMonitorSetup_yyyyMMdd_HHmm.exe` exists.
- [ ] Running the installer does not request admin rights by default.
- [ ] Start Menu shortcut launches Serial Monitor.
- [ ] Optional desktop shortcut launches Serial Monitor when selected.
- [ ] xterm loads.
- [ ] Settings/Profile works.
- [ ] MOCK connect works.
- [ ] Logs and profiles are written under `%LOCALAPPDATA%\SerialMonitor`.
- [ ] Uninstall removes installed app files.
- [ ] Uninstall does not delete `%LOCALAPPDATA%\SerialMonitor`.
- [ ] No MSIX/AppX package is produced.
