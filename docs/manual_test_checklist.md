# Manual Regression Test Checklist

Use this checklist before release candidates and after UI/layout changes. Prefer
`MOCK` for repeatable checks, then repeat critical connect/TX/logging checks on
real hardware when available.

## Startup

- [ ] Start the app.
- [ ] Confirm the window opens without an unhandled exception.
- [ ] Confirm the Port list includes one `MOCK` entry.
- [ ] Confirm bottom health shows `HEALTH OK` or a clear non-stale warning.
- [ ] Confirm Diagnostics can be opened.

## MOCK Connect And Disconnect

- [ ] Select `MOCK`.
- [ ] Click Connect.
- [ ] Confirm connection state changes to connected.
- [ ] Confirm RX logs start appending.
- [ ] Confirm FileWriter running state is true in Diagnostics.
- [ ] Disconnect.
- [ ] Confirm FileWriter stops cleanly.
- [ ] Reconnect to `MOCK`.
- [ ] Confirm no duplicate mock streams, duplicate events, or duplicate log rows.

## Real COM Connect Placeholder

- [ ] Connect a known serial device.
- [ ] Select its COM port and baud rate.
- [ ] Connect and confirm RX logs arrive.
- [ ] While connected, switch Terminal/HEX mode and confirm the app briefly
  reconnects to the same port with the new mode active.
- [ ] In HEX mode, change the HEX group timeout and confirm the same automatic
  reconnect applies the new native timeout.
- [ ] Send a harmless command.
- [ ] Disconnect and reconnect.
- [ ] Confirm no app crash or stale connection state.

## Bidirectional COM Bridge

- [ ] Confirm the configured com0com pair `COM4 <-> COM5` is present.
- [ ] Connect Serial Monitor to the real device COM port.
- [ ] Open the Bridge tab and select `COM4` as the app-side virtual port.
- [ ] Start the bridge and confirm `BRIDGE ON` appears in both the top and bottom status areas.
- [ ] Open an external controller or analyzer on `COM5`, not `COM4`.
- [ ] Confirm real-device RX bytes arrive unchanged at the external program.
- [ ] Send binary/HEX data from the external program and confirm the exact bytes reach the device.
- [ ] Confirm virtual-to-device traffic appears as `[BRIDGE]` TX in the app log.
- [ ] In Terminal mode with UTF-8 selected, send bytes `45 52 52 4F 52` and
  confirm the log shows `[BRIDGE] ERROR`, a Terminal `TxOnly` rule fires, and an
  equivalent HEX rule does not fire.
- [ ] In HEX mode, send bytes `45 52 52 4F 52` and confirm the log shows
  `[BRIDGE] 45 52 52 4F 52`, a HEX `TxOnly` rule fires, and an equivalent
  Terminal rule does not fire.
- [ ] In Terminal mode, split one UTF-8 multibyte character across consecutive
  bridge writes and confirm it is decoded without replacement characters.
- [ ] Repeat with CP949 and confirm a Terminal keyword spanning consecutive
  bridge chunks still matches.
- [ ] Change RX encoding and Terminal/HEX display modes; confirm forwarded bytes remain unchanged.
- [ ] Pause rendering and minimize the app while sending sustained virtual-to-device traffic; confirm the device continues receiving without bridge transport drops.
- [ ] If the UI-only bridge log queue is forced to overflow, confirm only its UI drop counter increases while transport byte counts continue.
- [ ] While Bridge is OFF, confirm Diagnostics reports raw bridge priority OFF and the normal awaited RX pipeline remains active.
- [ ] While Bridge is ON, force parser/UI overload and confirm raw bridge traffic continues while only the bridge-priority parser/log drop counter increases.
- [ ] Stop the external program temporarily and confirm bridge backlog/drop/error counters remain bounded and visible.
- [ ] While bridge traffic is active, request one manual TX and confirm the UI
  shows `TX waiting for bridge idle`; verify a second button/Enter/saved/history/
  shortcut request is rejected as Busy and the first payload is sent once after
  25 ms of global bridge idle.
- [ ] Confirm command sequences cannot start while Bridge is ON.
- [ ] Saturate the device-to-virtual queue and confirm the bridge alone stops
  with `Bridge stopped: virtual COM consumer too slow`, while the device COM and
  normal RX remain connected.
- [ ] Check Diagnostics for queued chunks/bytes, oldest age, replay delay/
  lateness, overflow reason, last activity, and manual TX state.
- [ ] Stop the bridge and confirm the `BRIDGE ON` indicators disappear.
- [ ] Disconnect the device and confirm the bridge stops before the device port closes.

## Xterm Log View

- [ ] Confirm xterm log lines append smoothly.
- [ ] Resize the app and confirm the xterm area fits the available space.
- [ ] Drag-select multiple xterm lines.
- [ ] Press Ctrl+C.
- [ ] Paste into Notepad and confirm selected text copied.
- [ ] Click Pause rendering and confirm visual append pauses.
- [ ] Confirm file/event counters continue increasing while paused.
- [ ] Resume rendering.
- [ ] Click Clear and confirm only the visible terminal clears.

## TX Manual Command

- [ ] Connect to `MOCK` or hardware.
- [ ] Enter a command in TX.
- [ ] Choose TX ending.
- [ ] Click Send.
- [ ] Confirm a TX line appears in xterm.
- [ ] Confirm the TX line appears in the serial log file.
- [ ] Confirm Send is disabled or fails gracefully while disconnected.

## Saved Commands

- [ ] Add a saved command named `reboot` with command text `reboot`.
- [ ] Confirm a compact `reboot` quick-send button appears.
- [ ] Connect and click `reboot`.
- [ ] Confirm TX logging works.
- [ ] Edit `reboot` and confirm the chip updates.
- [ ] Delete `reboot` and confirm it disappears.
- [ ] Confirm add/edit/delete works while disconnected.

## Saved Command Shortcut

- [ ] Add or edit a saved command with shortcut `Ctrl+1`.
- [ ] Focus the main window or xterm and press Ctrl+1.
- [ ] Confirm the command sends through the normal TX path.
- [ ] Focus a text input and press Ctrl+1.
- [ ] Confirm the shortcut does not interfere with typing.

## Command Sequences

- [ ] Open the `Sequences` tab.
- [ ] Add a sequence named `Boot Check`.
- [ ] Add steps:
  - `version`, delay 300 ms
  - `status`, delay 300 ms
  - `help`, delay 300 ms
- [ ] Confirm the selected sequence is chosen through the compact selector.
- [ ] Confirm steps are visible and scrollable in small-height windows.
- [ ] Edit a step and confirm the row updates.
- [ ] Move a step up and down.
- [ ] Delete a step.
- [ ] Connect to `MOCK`.
- [ ] Run the sequence.
- [ ] Confirm TX lines appear in order.
- [ ] Switch to HEX mode, add a sequence step such as `AA 55`, run it, and
  confirm the device receives bytes `0xAA 0x55` rather than ASCII text.
- [ ] Stop during execution and confirm it stops safely.
- [ ] Save Profile, restart, and confirm the sequence is restored.

## Markers

- [ ] Click Quick Mark.
- [ ] Confirm `MARK > User marker` appears in xterm.
- [ ] Open Custom Mark and enter `test start`.
- [ ] Confirm `MARK > User marker: test start` appears.
- [ ] Press Ctrl+M and confirm a default marker appears.
- [ ] Confirm markers are not sent to the serial port.
- [ ] Confirm markers are searchable.
- [ ] Confirm markers appear in the serial log file.

## Sessions

- [ ] Open Settings > Log or Profile section containing session controls.
- [ ] Set session name `inverter test`.
- [ ] Confirm `MARK > Session start: inverter test` appears.
- [ ] End the session if available.
- [ ] Confirm `MARK > Session end: inverter test` appears.
- [ ] Enable `Use session name in log file name`.
- [ ] Set a new session.
- [ ] Confirm current serial/event log paths include the sanitized session name.

## Event Detection And Context

- [ ] Connect to `MOCK`.
- [ ] Wait for WARN/ERROR/FAULT events.
- [ ] Open Events tab and confirm events append incrementally.
- [ ] Select an event.
- [ ] Open Context tab and confirm context appears immediately.
- [ ] Double-click an event and confirm Context opens immediately.
- [ ] Confirm BEFORE, MATCHED, and AFTER sections are visible.
- [ ] Select context text and press Ctrl+C.
- [ ] Click Copy Event Context and paste into Notepad.
- [ ] Confirm event log file contains the full context block.

## Event Auto And Latest

- [ ] Open Events tab.
- [ ] Confirm Auto is enabled by default if expected.
- [ ] Confirm new events keep the newest row visible.
- [ ] Turn Auto off and select an older event.
- [ ] Confirm selection is stable as new events arrive.
- [ ] Click Latest.
- [ ] Confirm the newest event is selected and scrolled into view.

## Event Notifications

- [ ] Confirm Tray, Sound, and Popup are all OFF for existing/default rules.
- [ ] Enable Popup for one MOCK event rule and keep cooldown at 30 seconds.
- [ ] Confirm a matching MOCK event shows one non-blocking popup after about 1 second.
- [ ] Confirm the popup closes automatically after about 8 seconds.
- [ ] Generate repeated matches and confirm they are grouped into at most one notification per 30 seconds.
- [ ] Enable Sound and confirm one sound plays per grouped notification, not once per event.
- [ ] Enable Tray and confirm a Windows tray balloon appears.
- [ ] Disable all three options and confirm events continue to populate without any notification.

## Highlight Rules

- [ ] Open Rules tab.
- [ ] Confirm the rule editor and rule list label the selector as `Mode`, with
  `Terminal` and `HEX` choices (not `Match = Text/HEX`).
- [ ] In Terminal mode, enable one Terminal rule and one HEX rule whose keywords
  both represent the same incoming bytes; confirm only the Terminal rule creates
  events/highlights.
- [ ] Switch to HEX mode without changing either rule's Enabled state and confirm
  only the HEX rule creates events/highlights.
- [ ] Mark both rules as filters and confirm the Filter dropdown lists only rules
  for the current mode; switching modes must fall back to `ALL` if the selected
  filter belongs to the other mode.
- [ ] Toggle WARN highlight off.
- [ ] Confirm future WARN lines no longer use the WARN highlight color.
- [ ] Change ERROR highlight color.
- [ ] Confirm future ERROR lines use the new color.
- [ ] Add a highlight rule for `RESET`.
- [ ] Confirm the rule persists after Save Profile and restart.
- [ ] Receive a Terminal line, switch the retained RX view to HEX, and confirm
  HEX highlight/filter rules match its raw bytes in the rebuilt view.

## Search

- [ ] Search for `WARN`.
- [ ] Confirm match count updates.
- [ ] Click Next and Prev.
- [ ] Confirm xterm jumps/selects matches.
- [ ] Toggle Case and confirm behavior changes.
- [ ] Confirm logs continue appending during search.

## Search Results Tab

- [ ] Open Search tab.
- [ ] Confirm result rows are compact and stable.
- [ ] Confirm status appears in the toolbar, not as a result row.
- [ ] Confirm Auto refresh is off by default.
- [ ] Click Refresh and confirm results rebuild.
- [ ] Double-click a result and confirm xterm jumps/selects it.
- [ ] Confirm stale/manual status is clear while logs append.

## Settings Validation

- [ ] Enter invalid numeric values such as `abc`, `-1`, `0` where invalid, and
  an extremely large number.
- [ ] Confirm the app does not crash.
- [ ] Confirm a clear validation/status message appears.
- [ ] Confirm invalid values are not saved as active settings.
- [ ] Enter an invalid save directory and confirm it fails gracefully.
- [ ] Confirm safe settings still apply after validation failures.

## Profile Save, Load, Reset

- [ ] Change baud rate or TX ending.
- [ ] Disable one highlight rule.
- [ ] Add a saved command.
- [ ] Save Profile.
- [ ] Restart the app.
- [ ] Confirm saved settings are restored.
- [ ] Load Profile while disconnected.
- [ ] Confirm Load Profile is disabled or guarded while connected.
- [ ] Reset to Defaults while disconnected.
- [ ] Confirm safe defaults are restored.

## Log File Actions

- [ ] Connect to `MOCK`.
- [ ] Wait until serial log file is created.
- [ ] Generate an event so event log file is created.
- [ ] Open Settings > Log.
- [ ] Open current serial log.
- [ ] Open current event log.
- [ ] Open logs folder.
- [ ] Copy serial log path and paste into Notepad.
- [ ] Copy event log path and paste into Notepad.
- [ ] Confirm FileWriter remains running.
- [ ] Confirm EventDetector remains running.

## Mock Stress Mode

- [ ] Select `MOCK`.
- [ ] Start stress mode at a low rate.
- [ ] Confirm logs append and counters increase.
- [ ] Confirm missing sequence count stays 0.
- [ ] Increase lines/sec.
- [ ] Confirm the app remains responsive.
- [ ] Pause rendering.
- [ ] Confirm RX/File/Event counters continue increasing.
- [ ] Resume rendering.
- [ ] Stop stress mode.
- [ ] Reset stress counters.

## Health And Diagnostics

- [ ] Confirm bottom status shows `HEALTH OK` during a clean mock session.
- [ ] Confirm Diagnostics shows health reasons and detailed counters.
- [ ] Copy Diagnostics and paste into Notepad.
- [ ] If practical, force a warning/error counter and confirm health changes to
  `WARNING` or `ERROR`.
- [ ] Confirm stale profile or search-result states are not treated as health
  errors.
