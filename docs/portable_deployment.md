# Portable Deployment

Serial Monitor supports a no-install folder deployment for Windows PCs where
MSIX packages or installers are blocked by company policy.

## Build Type

- Unpackaged WinUI 3 app
- Folder/zip distribution
- Self-contained .NET publish
- Windows App SDK self-contained publish
- x64 runtime
- No MSIX
- No installer required
- No admin rights required

MSIX is intentionally not used because target company PCs may block MSIX
installation.

An optional normal `.exe` installer can also be built from this portable output
with Inno Setup. See `docs/installer_deployment.md`. The portable zip remains
the safest fallback when company security blocks installers.

## Publish Command

Use the helper script from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish_portable.ps1
```

The script publishes with these important properties:

```powershell
-p:WindowsPackageType=None
-p:SelfContained=true
-p:WindowsAppSDKSelfContained=true
-p:RuntimeIdentifier=win-x64
-p:PublishSingleFile=false
```

If `RuntimeIdentifier=win-x64` fails, the script retries with
`RuntimeIdentifierOverride=win-x64`.

## Output

The script creates:

- `release\SerialMonitorPortable`
- `release\SerialMonitorPortable_yyyyMMdd_HHmm.zip`
- `release\SerialMonitorPortable\README_PORTABLE.txt`

The portable folder must include `SerialMonitor.WinUI.exe`, runtime DLLs,
Windows App SDK files, local `Assets\xterm` files, local `Assets\context`
files, and bundled `Assets\FunBackgrounds` files. Keep the full folder together
when copying to another PC.

## Running On Another PC

1. Copy the zip to the target PC.
2. Unzip it.
3. Run `SerialMonitor.WinUI.exe` from the extracted folder.
4. Do not run the app from inside the zip.
5. Do not move individual DLLs or `Assets` folders away from the exe.

Logs and profiles remain user-local:

- Logs: `%LOCALAPPDATA%\SerialMonitor\logs`
- Default profile: `%LOCALAPPDATA%\SerialMonitor\profiles\default.json`
- Runtime diagnostics:
  `%LOCALAPPDATA%\SerialMonitor\diagnostics\last_runtime_error.txt`

## Target PC Notes

WebView2 Runtime may still be required on the target PC for the xterm log view
and event context viewer. Many Windows 10/11 systems already have it through
Microsoft Edge, but company images vary.

If the app does not start, check:

- Windows version
- Microsoft Edge WebView2 Runtime availability
- company security blocking copied `.exe` or `.dll` files
- missing files because the folder was copied incompletely
- antivirus quarantine of runtime or asset files

## Verification Checklist

- [ ] `release\SerialMonitorPortable` exists.
- [ ] `release\SerialMonitorPortable\SerialMonitor.WinUI.exe` exists.
- [ ] `release\SerialMonitorPortable\Assets\xterm\index.html` exists.
- [ ] `release\SerialMonitorPortable\Assets\context\index.html` exists.
- [ ] `release\SerialMonitorPortable\Assets\FunBackgrounds\default_cute_bg.jpg` exists.
- [ ] `release\SerialMonitorPortable_yyyyMMdd_HHmm.zip` exists.
- [ ] No `.msix`, `.msixbundle`, `.appx`, or `.appxbundle` file is produced.
- [ ] App starts from the release folder.
- [ ] xterm log view loads.
- [ ] Settings/Profile works.
- [ ] MOCK connect works.
- [ ] Logs are written under `%LOCALAPPDATA%\SerialMonitor\logs`.
