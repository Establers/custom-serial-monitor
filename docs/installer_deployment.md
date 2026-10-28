# Inno Setup Installer Deployment

Serial Monitor can be distributed as a normal `.exe` installer in addition to
the portable zip. This installer is optional and does not use MSIX.

## What This Installer Is

- Normal Inno Setup `.exe` installer.
- Installs the already-published portable app files.
- Per-user install by default, so admin rights are not required.
- Creates a Start Menu shortcut named `Serial Monitor`.
- Offers an optional desktop shortcut.
- Uses the app's bundled icon for the Setup and Uninstall executables.
- Lets the user choose the program installation folder, including another drive.
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
- Inno Setup 7, 6, or 5 installed in the default `Program Files` location, or
- pass an explicit compiler path with `-IsccPath`.

If `ISCC.exe` is not found, the script prints:

```text
Install Inno Setup or add ISCC.exe to PATH.
```

### Inno Setup License

The installer build tool has its own license and is not covered by this
project's MIT license. Inno Setup permits non-commercial use; its publisher
requests that commercial users purchase an appropriate license. Confirm the
build user's or CI environment's Inno Setup license before producing a
commercial release. This does not affect the portable zip, which is built by
the .NET SDK without Inno Setup.

## Outputs

Portable output remains:

- `release\SerialMonitorPortable`
- `release\SerialMonitorPortable_yyyyMMdd_HHmm.zip`

Installer output:

- `release\installer\SerialMonitorSetup_yyyyMMdd_HHmm.exe`
- `release\SerialMonitorSetup.exe` and its `.sha256` checksum (fixed download name)

No `.msix`, `.msixbundle`, `.appx`, or `.appxbundle` package should be produced.

The installer recursively includes the portable output, including the bundled
JetBrains Mono WOFF2 files and `Assets\xterm\fonts\OFL.txt`. It does not install
the font system-wide.

## Install Location

The installer uses a per-user path:

```text
%LOCALAPPDATA%\Programs\SerialMonitor
```

This is the initial destination. The directory selection page stays visible for
fresh installs and upgrades, so users can choose another writable location, such
as `D:\Apps\SerialMonitor`. The CLI then lives at
`D:\Apps\SerialMonitor\tools\serialctl.exe`.

## Plugin Source and AI Client Registration

Setup installs the GUI, `tools\serialctl.exe` and a local plugin marketplace at
`{app}\agent-plugin`. This source contains the `serial-monitor` plugin and the
`serial-monitor-tools` marketplace catalog. An upgrade refreshes that source along
with the program. It does not silently install a plugin in an AI client, overwrite
client configuration/caches, or replace legacy standalone skills.

Use the app's **Plugin** tab to copy the selected client's installation and update
commands. Codex desktop/CLI, Claude Code and Copilot CLI/VS Code use client-specific
registration and reload behavior. A local source needs a host reload/restart after
app setup; a Git-installed plugin is updated through its Git marketplace instead.
See the [plugin guide](agent_plugins.md). `SerialMonitorPlugin.zip` provides the same
marketplace without application binaries. `SerialMonitorSkills.zip` remains a
[legacy standalone fallback](agent_skills.md).

The launcher discovers the current CLI through an explicit `SERIAL_MONITOR_CLI`,
nearby application files, installation registration, the default path or PATH/build
locations. A custom Inno Setup destination is recorded for this lookup. If a local
marketplace registration still points at an old application folder after a move,
update that registration's source path too. Git/ZIP plugins paired with a portable
application in another folder can set `SERIAL_MONITOR_CLI` in the actual AI host's
environment. Already-running desktop processes do not inherit an unrelated shell's
new environment values.

Existing manual MCP registrations retain their old executable command. Update
only the superseded registration when migrating; do not leave a duplicate server
unless intentionally needed. Legacy skill users can rerun their existing installer
with the actual new `-CliPath` and the same destination options.

## Reinstalling While MCP Is Running

Closing the GUI does not stop `serialctl mcp`: its lifetime belongs to the AI
client that opened its stdio connection. The process can keep the installed
`tools\serialctl.exe` locked even after the GUI closes.

Setup uses Inno Setup's standard Windows Restart Manager integration with
`CloseApplications=force`. It detects applications using the files being replaced
and offers to close them. Restart Manager attempts shutdown first and can force
an unresponsive process to exit after about 30 seconds. Allow that wait before
assuming Setup is stuck. Close the GUI before updating to finish serial work and
flush logs normally.

`RestartApplications=no` leaves closed applications stopped after installation.
Start the GUI and reconnect/restart `serial-monitor` in the AI client afterward
so the client establishes a new stdio connection. Existing AI client registrations
and separately installed standalone skills are retained; local plugin sources are
refreshed, while client caches follow their own reload/update rules.

These are built-in installer settings; see Inno Setup's
[CloseApplications](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm)
and [RestartApplications](https://jrsoftware.org/ishelp/topic_setup_restartapplications.htm)
documentation.

## User Data

The installer does not change the app's user data locations:

- Logs: `%LOCALAPPDATA%\SerialMonitor\logs`
- Default profile: `%LOCALAPPDATA%\SerialMonitor\profiles\default.json`
- Runtime diagnostics:
  `%LOCALAPPDATA%\SerialMonitor\diagnostics\instance-<pid>-<launch-id>\last_runtime_error.txt`

Each launch creates a separate `instance-*` diagnostics folder. Check its
`last_startup.txt` to identify the launch by startup time; `last_runtime_error.txt`
exists only after a runtime error. Root-level diagnostics `.txt` files are legacy
records and are not updated by this version.

Uninstall removes installed program files only. It must not delete logs,
profiles, diagnostics, or other user data under `%LOCALAPPDATA%\SerialMonitor`.
It also leaves AI client registrations, installed plugin caches and separately
installed standalone skill folders intact. A local marketplace pointing inside
the removed application directory must be unregistered in that AI client when
no longer needed.

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
- [ ] Installed `Assets\xterm\fonts\OFL.txt` exists beside the bundled font files.
- [ ] Running the installer does not request admin rights by default.
- [ ] The program directory page is visible on fresh install and upgrade.
- [ ] A custom program location, such as `D:\Apps\SerialMonitor`, launches correctly.
- [ ] `agent-plugin\.claude-plugin\marketplace.json` and the complete `agent-plugin\plugins\serial-monitor` source are installed.
- [ ] Installation and upgrade leave AI client configuration/caches and separately installed legacy skills unchanged.
- [ ] Upgrade closes an installed `serialctl mcp` process and replaces its EXE
      without a Windows restart; a CLI running from another folder stays running.
- [ ] After upgrade, reconnecting MCP in the AI client starts the installed CLI.
- [ ] Plugins can be installed later from the bundled local source, standalone plugin ZIP or published Git marketplace.
- [ ] A local-source plugin reloads after app upgrade using each host's documented reload/restart behavior.
- [ ] A Git-installed plugin continues to use its chosen Git source instead of being silently replaced by setup.
- [ ] Custom installation paths are resolved by the launcher; a relocated local source registration is updated intentionally.
- [ ] Legacy skills can still be installed from the separate skill ZIP with the actual CLI path.
- [ ] Start Menu shortcut launches Serial Monitor.
- [ ] Optional desktop shortcut launches Serial Monitor when selected.
- [ ] xterm loads.
- [ ] Settings/Profile works.
- [ ] MOCK connect works.
- [ ] Logs and profiles are written under `%LOCALAPPDATA%\SerialMonitor`.
- [ ] Uninstall removes installed app files.
- [ ] Uninstall does not delete `%LOCALAPPDATA%\SerialMonitor`.
- [ ] No MSIX/AppX package is produced.
