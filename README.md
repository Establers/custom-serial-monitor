# Serial Monitor

Windows serial monitor for embedded and RTOS debugging.

This project is a WinUI 3 desktop application designed for long-running serial
monitoring, command transmission, event capture, and log-file analysis. The main
goal is stable daily debugging of embedded devices, not a touch-first or
marketing-style UI.

## Key Features

- Real serial I/O through `RJCP.SerialPortStream`.
- Local WebView2/xterm.js main log view with selectable terminal-style text.
- Manual TX command sending with configurable line endings.
- Saved TX commands with add/edit/delete, quick-send buttons, and shortcuts.
- Command sequences for repeatable RTOS test workflows.
- User markers and session start/end markers inserted into the log timeline.
- Optional session names in new serial/event log filenames.
- Configurable event detection rules.
- Event context capture with before/matched/after lines.
- WebView2 event context viewer with terminal-like styling.
- Configurable xterm highlight rules.
- Visible-buffer search with Next/Prev and xterm jump/selection.
- Search Results inspector tab with manual refresh by default.
- Asynchronous plain-text serial and event file logging.
- JSON profile save/load/reset.
- MOCK port and opt-in mock stress mode with sequence-loss counters.
- Compact health summary and copyable diagnostics.

## Basic Usage

1. Select a serial port, or choose `MOCK` for local testing.
2. Select the baud rate and connect.
3. Watch logs in the xterm.js main log view.
4. Send a manual TX command from the TX row.
5. Use saved command chips for frequent commands.
6. Add a quick marker with `Mark` or `Ctrl+M`.
7. Use `Events` and `Context` to inspect detected keywords and captured context.
8. Use `Sequences` to run multi-command RTOS checks.
9. Save the current profile when settings, rules, commands, or sequences change.

## Build

From the repository root:

```powershell
dotnet build SerialMonitor.WinUI\SerialMonitor.WinUI.csproj -p:Platform=x64
```

The WinUI project and solution are under `SerialMonitor.WinUI`.

## Portable Publish

For company PCs that cannot install MSIX packages, create a no-install portable
folder/zip build:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish_portable.ps1
```

Output is written to `release\SerialMonitorPortable` and
`release\SerialMonitorPortable_yyyyMMdd_HHmm.zip`. MSIX is intentionally not
used. See `docs/portable_deployment.md`.

## Optional Installer

To create a normal non-MSIX `.exe` installer from the portable output, install
Inno Setup and run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_installer.ps1
```

Output is written to `release\installer\SerialMonitorSetup_yyyyMMdd_HHmm.exe`.
The installer uses a per-user install path by default and does not delete logs or
profiles under `%LOCALAPPDATA%\SerialMonitor`. See
`docs/installer_deployment.md`.

## Runtime Notes

- Target platform: Windows desktop.
- WebView2 Runtime is required for the xterm.js log view and event context view.
- Real serial ports use `RJCP.SerialPortStream`.
- The `MOCK` port can be used without hardware for UI, logging, event, search,
  sequence, and stress testing.
- File and event logs are plain text. Visual xterm coloring does not write ANSI
  escape codes to saved logs.

## Default File Locations

- Serial/event logs:
  `%LOCALAPPDATA%\SerialMonitor\logs`
- Default profile:
  `%LOCALAPPDATA%\SerialMonitor\profiles\default.json`
- Last runtime error:
  `%LOCALAPPDATA%\SerialMonitor\diagnostics\last_runtime_error.txt`

## Documentation

- Manual regression checklist: `docs/manual_test_checklist.md`
- Known limitations: `docs/known_limitations.md`
- Code review and improvement plan: `docs/code_review.md`
- Portable deployment: `docs/portable_deployment.md`
- Optional installer deployment: `docs/installer_deployment.md`
- Architecture: `docs/architecture.md`
- Requirements: `docs/requirements.md`
- Historical UI/UX audit: `SerialMonitor.WinUI/docs/ui_ux_audit.md`
- Draft release notes: `docs/release_notes_draft.md`
