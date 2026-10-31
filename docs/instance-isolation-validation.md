# Multiple-instance isolation

The runtime independence boundary is one app launch. Settings/profile persistence
is shared; loading a profile is an explicit action. TX recall history, search
recall history and the last search text are saved separately for each COM port.

Choosing COM7 restores COM7's TX/search history in any window, regardless of launch
or shutdown order. A new port starts empty. Switching ports captures the outgoing
history in memory before restoring the incoming port, and profile saves retain all
ports used by that window. While connected, history stays with the connected port
even if a selection is pending for reconnect. MOCK and the no-port state use
separate keys. No launch-slot lease or numbered window title is used.

The shared profile stores `PortHistories`, with at most 100 TX commands and 50
search terms per port. Saves merge changes against immutable local baselines under
the same cross-process file lease used for profiles. Other ports are preserved;
two windows using one port merge their additions, and an unchanged stale save
cannot resurrect a cleared entry. Missing ports in a local snapshot do not delete
disk histories. History switches do no disk I/O and no work is added to serial RX.

At the first successful save of an older profile, its exact bytes are backed up
beside it as `default.json.before-port-history-<hash>.json` before atomic profile
replacement. A failed backup aborts the save. Common and numbered-slot histories
are imported once to the previously saved port because slots contain no port
identity. The originals remain in `LegacyInputHistory` and `InstanceHistories`,
with the complete original JSON in the backup even if the merged history exceeds
normal UI retention. The migration marker prevents later clears from reimporting
archived entries. Other settings and saved sequences retain shared persistence.
Use the updated build for concurrent launches; older builds do not retain the new
port dictionary on save. A changed COM number is a separate history, and the old
port's history remains stored.

## Audit

| Component | Ownership / external sharing |
| --- | --- |
| Serial RX/TX, reconnect, bridge | Per-instance services, channels, workers and cancellation sources. COM handles belong to the owning instance. |
| Parsing and Terminal/HEX mode | Per-instance pipeline, decoder and line parser. |
| Event detection and sequences | Per-instance detector, runner, channels, counters and cancellation. |
| Log history, filter/search, selection, scroll, font, theme, pause/minimize | Per-instance view models, queues and xterm document. |
| WebView2 browser/profile/cache/storage | Unique launch directory and ExclusiveUserDataFolderAccess. Never falls back to a shared environment. |
| Runtime errors and health | Unique launch directory; another launch cannot overwrite or clear the files read by health. |
| Serial file capture | Independent writer and queue. Automatic names use atomic CreateNew and collision suffixes. Explicit existing names are rejected. |
| Tray notifications | Identified by each window's HWND plus its icon ID. |
| Default profiles / update preferences | Shared persistent values; update preference writes use an OS file lock and merge changes. |
| TX/search recall histories and last search text | Persisted per COM port; profile saves merge local changes with disk while holding the profile file lease. |
| Clipboard / Windows theme / hardware / CPU / disk | OS resources. Clipboard changes are global; System theme follows Windows; one COM port cannot be independently owned by two launches. |

## Automated checks

```powershell
dotnet test SerialMonitor.WinUI/SerialMonitor.WinUI.sln -c Debug -p:Platform=x64
node scripts/test_xterm_theme_isolation.cjs
node scripts/test_xterm_instance_isolation.cjs
```

The browser scripts require Playwright and Edge; NODE_PATH can point at the bundled
runtime packages. They use disposable browser profiles and do not use the user's
browser. The theme test deliberately shares one browser context to verify the
per-page theme remains independent even when browser preferences change. The
instance test launches separate Edge processes/profiles and checks retained
history, clear, selection, scroll, font, capacity, theme, reload, browser storage,
and continued appends after the other browser exits.

RuntimeComponentIsolationTests exercises concurrent service instances: MOCK TX
and reconnect, visible buffer changes, stopping event detection, independent
sequence cancellation, and simultaneous disk captures. RuntimeInstanceStorageTests
checks distinct cache/diagnostic paths, protection of active caches during another
startup/shutdown, abandoned-cache cleanup, preservation of profiles/diagnostics,
and cancellation. Real Windows file/directory locks also verify that failed cache
deletion preserves the owner marker and succeeds on retry after the lock releases.

InputHistoryIsolationTests covers both shutdown orders, three concurrent port
writers, writers using the same port, stale saves after clears, canonical COM
names, MOCK/no-port separation, pending reconnect selections, retention limits,
and production view-model TX/search Up/Down navigation after switching ports. It
also verifies legacy common/slot import, preservation of the exact original JSON,
migration idempotence and refusal to overwrite when the backup fails.

A local native WebView2 smoke fixture also created two real controllers with the
same unique-directory/exclusive-access policy. Their browser process IDs differed;
after the first BrowserProcessExited event, the second controller retained its
light theme and old log and accepted new data. The fixture used a hidden native
parent and timer-driven frame callbacks to exercise appends without opening UI.
Its source is saved locally under artifacts/webview-isolation (generated, ignored).

These browser fixtures exercise the shipped xterm assets. They are not a complete
UI test of two WinUI windows or a test using two physical serial devices. Use the
multi-instance steps in manual_test_checklist.md for that final hardware/UI check.
