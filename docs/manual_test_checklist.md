# Manual Regression Test Checklist

- [ ] In Settings, select System, Dark, and Light under Appearance. Verify window,
  title bar, terminal, selection and menus follow the choice without clearing logs
  or restarting serial/file logging. Restart normally and verify the choice is
  restored. With System selected, change the Windows theme; also check Windows
  high contrast remains legible.
- [ ] Run two app instances. Select Light in one and Dark in the other, then
  switch either instance's theme repeatedly. Verify the other instance's terminal,
  selection, scrollbar and context menu keep their own theme. Repeat with System
  in one instance, change the Windows theme, and verify only System follows it.
  Confirm retained logs and ongoing serial/file logging survive each change.
- [ ] Connect two instances to different COM ports (or MOCK). In one, clear,
  filter, search, change font, pause/resume, reconnect, stop a sequence, minimize
  and exit. Verify the other keeps receiving, logging and detecting its own events
  with unchanged terminal history, selection and scroll position. Verify its
  health does not show the first instance's runtime errors.

Use this checklist before release candidates and after UI/layout changes. Prefer
`MOCK` for repeatable checks, then repeat critical connect/TX/logging checks on
real hardware when available.

## Startup

- [ ] In Terminal mode, split UTF-8 Korean bytes after the first/second byte of
  a character at a partial flush boundary (128 bytes or 250 ms idle). Repeat
  with CP949 split after the leading byte. Verify the combined text is intact,
  raw RX is unchanged, and an incomplete character alone does not increase the
  decode-error count. A truly incomplete final character must still be reported
  when the line/session ends; a new session must not inherit its pending bytes.
- [ ] Start two windows using the shared profile. Register a sequence in A,
  then save settings and exit B without reloading its stale sequence list.
  Repeat the stale save, restart and verify A's sequence remains. Delete a
  sequence in one window and save an unchanged older copy in the other; verify
  it is not resurrected. Concurrent different edits to the same sequence must
  report a conflict and preserve the last committed file.

- [ ] With the GUI running, use `serialctl sequence save --file check.json` to
  register a documented device sequence. Verify it appears in the GUI, causes
  no TX, and survives app restart. Duplicate names require `--overwrite true`.
  A failed profile save must leave the GUI sequence collection unchanged.
- [ ] Start via CLI or `serial_sequence_start`, query the returned runId and
  verify ordered, one-time TX plus progress in the GUI. Close the initiating
  CLI after start; the accepted run must continue. Try GUI Stop, AI stop and
  `sequence cancel --run ID`; control returns after RX has been saved. Manual
  TX, sequence editing, profile replacement and Bridge must be blocked while
  running. Disconnect during a step; no further steps or automatic TX retries.
- [ ] In Sequences, create three MOCK commands with a visible delay and disable
  the middle step. Run twice: only the first and third commands/delays execute,
  progress counts four active steps and the displayed editor step numbers stay
  1 and 3. Disable all: Run is unavailable. Re-enable and export/reload/save/restart:
  checkbox state and all original steps remain. A legacy JSON without IsEnabled
  opens with all steps enabled. CLI/MCP also omits disabled commands and delays.
- [ ] Copy a step and a sequence, then edit the copy. Verify the original keeps
  its command, mode, enabled state and metadata. The list shows only command
  text on one line, including when a step has a name or multiline payload.
  In the step dialog, change Global/Terminal/Hex; invalid explicit HEX cannot be saved.
- [ ] Use the single sequence play/pause button: Play starts, the icon changes
  to Pause, and clicking it waits for the current send/delay before pausing.
  The icon changes back to Play; clicking it continues the same run. Cancel a
  pending pause by clicking Play again; Stop cancels while paused. Pause during automatic
  reconnect must still hold after reconnect. Successful TX must not replay.
  Check editing is locked during the run and CLI/MCP exposes Stop only.
- [ ] Compare optional expected `abcd` with actual `abcd` and `ab12c3d4`.
  Verify `matched` and `not_matched` respectively, without treating a miss as
  product failure or retrying. Follow captureId/nextOffset to inspect complete
  RX, including data after an early match and between steps. RX gaps/errors
  must make match evidence inconclusive. Repeat with literal HEX bytes.

- [ ] Enable Cute background mode, choose a JPG/PNG (also try a path with spaces
  and Korean text), and enter opacity values 0, 1, 37, 73 and 100 percent.
  Confirm 0 hides the image and 100 shows it fully, with intermediate values
  changing its visibility. The main xterm surface remains opaque.
- [ ] Confirm the opacity editor matches other Settings inputs (200 x 24), has
  a separate `%` suffix and no spin buttons. While focused/hovered, scroll the
  mouse wheel and press Up/Down; the value must stay unchanged. Enter/paste
  `101`, `1000` and a very long integer: each must become `100`. Try letters,
  `12.5`, `-1`, `25%` and mixed pasted text: reject the entire edit and keep the
  previous value. Clear the field, then press Enter or leave it: restore the last
  valid value. Select all and replace with `0`, `37`, `100`; verify immediate
  application and correct restoration after restarting the app.
- [ ] Close normally without pressing Save Profile, restart, and confirm the
  background mode, image path and opacity are restored. Repeat while receiving
  MOCK logs and with background mode disabled. Temporarily rename the image,
  restart, and confirm the default image appears while the selected path stays
  saved. Restore the file and restart to confirm the custom image returns.
- [ ] In Settings, verify every serial selector, appearance selector and UI
  selector uses the same width and aligned label/control columns. Check the
  longest values (OnePointFive, DtrRtsXOn, JetBrains Mono (Bundled), and
  yyyy-MM-dd HH:mm:ss.fff) fit fully including the dropdown arrow. Repeat for
  Bridge and Test selectors and the HEX timeout input.
- [ ] Narrow the inspector/window and verify setting fields keep their width
  and remain reachable by horizontal scrolling. Labels, paths, explanations
  and status text should wrap instead of using an ellipsis or clipping a line.

- [ ] Start the app.
- [ ] Confirm the bridge is `BRIDGE OFF` after startup, including when the saved
  profile previously contained an enabled bridge and the device COM auto-connects.
- [ ] Disconnect/reconnect the device and confirm the bridge stays OFF until
  `Start bridge` is pressed explicitly.
- [ ] Confirm the window opens without an unhandled exception.
- [ ] Confirm the Port list includes one `MOCK` entry.
- [ ] Confirm bottom health shows `HEALTH OK` or a clear non-stale warning.
- [ ] Hover over the footer and confirm the health reason and counter legend appear.
- [ ] Right-click the footer, copy status, and paste it into Notepad.

## Window Names

- [ ] Select a port and click the name button at the far right of the top row.
  Verify the input receives focus and Enter applies the name to the button and
  native title (`Main board · COM3 - Serial Monitor`).
- [ ] Edit the name, press Esc or click outside, and verify the previous name remains.
- [ ] Apply an empty name and verify the title falls back to the COM port only.
- [ ] Rename while MOCK RX and file logging are active; verify both continue and
  the log filename and command history remain unchanged.
- [ ] Narrow the window and verify the button remains visible; long names must
  show an ellipsis with the full name available in the tooltip.
- [ ] Switch ports and restart; verify names restore for their respective ports.
- [ ] Run two instances on different ports and save both names, then exit/restart.
  Verify neither name was lost or copied to the other port.

## MOCK Connect And Disconnect

- [ ] Right-click the top Logging button and open the log file location. While
  logging, verify it opens the active file's folder without toggling logging.
  After stopping, verify it opens the last log's folder; before any log exists,
  verify it opens the configured Save dir. Repeat with spaces and Korean text
  in the folder path, and after changing Save dir while a previous log exists.
- [ ] Pause the log display and verify the play icon uses the existing error-red
  color. Resume and verify the pause icon retains its original foreground.
  Repeat in Light and Dark and switch theme while paused; high contrast must
  retain the app's accessible error color.

- [ ] Select `MOCK`.
- [ ] Click Connect.
- [ ] Confirm connection state changes to connected.
- [ ] Confirm RX logs start appending.
- [ ] Confirm the footer changes to `File ON` while file logging is active.
- [ ] Disconnect.
- [ ] Confirm FileWriter stops cleanly.
- [ ] Reconnect to `MOCK`.
- [ ] Confirm no duplicate mock streams, duplicate events, or duplicate log rows.

## Real COM Connect Placeholder

- [ ] Connect a known serial device.
- [ ] Select its COM port and baud rate.
- [ ] Connect and confirm RX logs arrive.
- [ ] While connected, switch Terminal/HEX mode repeatedly and confirm the same
  COM port remains selected and connected while new data uses the active mode.
- [ ] With LOG ON, switch Terminal/HEX in both directions: visible history and
  search results clear, the same log file retains pre-switch records and receives
  post-switch records, and pending bridge/xterm batches do not restore old history.
  Repeat while the view is paused, then resume. Selecting the current mode or
  changing only the HEX timeout must not clear history.
- [ ] In Terminal mode, drag across two timestamped log lines and confirm the
  tooltip shows only `Δt: <duration>`; selecting within one logical line shows
  `Δt: 0ms`.
- [ ] Keep dragging in Terminal mode while new MOCK lines append or the terminal
  auto-scrolls; confirm the delta tooltip remains visible until mouse release.
- [ ] Hide visible timestamps and repeat the Terminal selection; confirm the
  delta tooltip still works and copied text contains no tooltip metadata.
- [ ] In HEX mode, drag across known bytes on two log lines and confirm the
  tooltip uses two lines: `Len: <bytes>` followed by `Δt: <duration>`. Confirm
  the byte count matches the previous selection-length behavior.
- [ ] In HEX mode, change the HEX group timeout and confirm it applies without
  disconnecting the COM port.
- [ ] Confirm `RX BUSY -- · IDLE -- · PEAK -- · 1m` appears to the right of the HEX timeout
  while disconnected in HEX mode.
- [ ] Connect in HEX mode and confirm the bus meter changes to a warm-up duration,
  then shows `1m` after 60 seconds.
- [ ] During warm-up, confirm BUSY and IDLE percentages are available after the
  first sample, PEAK remains `--`, and duration is shown as `<seconds>/1m`.
- [ ] Reset immediately before a timer tick, inject a short high-rate burst, and
  confirm it does not become a false full-second PEAK when the first minute
  completes.
- [ ] Generate one high-rate RX burst followed by a lower steady rate and confirm
  PEAK holds the busiest approximately one-second interval, then rolls out 60
  seconds after that interval.
- [ ] Press Clear in HEX mode and confirm BUSY/IDLE/PEAK return to the collecting
  state and start a new warm-up window. Confirm file logging continues normally.
- [ ] Confirm the bus meter disappears in Terminal mode and restarts its warm-up
  window when switching back to HEX mode.
- [ ] With known continuous RX traffic, confirm the BUSY estimate is consistent
  with `RX bytes × framing bits ÷ baud ÷ observed seconds`.
- [ ] Send local TX traffic without RX echo and confirm it does not increase the
  RX BUSY estimate.
- [ ] Send a harmless command.
- [ ] Disconnect and reconnect.
- [ ] Confirm no app crash or stale connection state.
- [ ] Enable automatic reconnect, unplug the adapter during RX, and confirm the
  top button changes to `Cancel Reconnect` while retries use 1, 2, 5, then 10
  second delays.
- [ ] Reinsert the adapter with the same COM number and confirm RX resumes once,
  without duplicate rows or duplicate observers.
- [ ] With LOG ON, repeat the unplug/reinsert test and confirm logging continues
  in the same active file rather than creating or appending another named file.
- [ ] Start a bridge and a command sequence separately, force an unexpected
  disconnect, and confirm each is stopped and not automatically resumed.
- [ ] Run a sequence whose current step has a 600-second delay, unplug the
  adapter, and confirm the sequence leaves Running immediately without waiting
  for the COM port to return.
- [ ] During retry, click `Cancel Reconnect` and confirm retries stop, background
  connection resources are cleaned up, and a later manual Connect succeeds.
- [ ] Let one reconnect open attempt fail, then close the app while it is waiting
  for the next retry. Confirm the process exits and no later COM open occurs.
- [ ] Disable automatic reconnect, force another fault, and confirm no retry is
  scheduled. Confirm a manual Disconnect never schedules a retry.

## Bidirectional COM Bridge

- [ ] Confirm the configured com0com pair `COM4 <-> COM5` is present.
- [ ] Connect Serial Monitor to the real device COM port.
- [ ] Open the Bridge tab and select `COM4` as the app-side virtual port.
- [ ] Start the bridge and confirm `BRIDGE ON` appears in both the top and bottom status areas.
- [ ] While the bridge is running, click Refresh and confirm the selected virtual port and route remain unchanged.
- [ ] Open an external controller or analyzer on `COM5`, not `COM4`.
- [ ] Confirm real-device RX bytes arrive unchanged at the external program.
- [ ] Send binary/HEX data from the external program and confirm the exact bytes reach the device.
- [ ] Confirm virtual-to-device traffic appears as RX in the app log without a
  `[BRIDGE]` prefix.
- [ ] In Terminal mode with UTF-8 selected, send bytes `45 52 52 4F 52` and
  confirm the log shows `ERROR`, a Terminal `RxOnly` rule fires, and an
  equivalent HEX rule does not fire.
- [ ] In HEX mode, send bytes `45 52 52 4F 52` and confirm the log shows
  `45 52 52 4F 52`, a HEX `RxOnly` rule fires, and an equivalent
  Terminal rule does not fire.
- [ ] In Terminal mode, split one UTF-8 multibyte character across consecutive
  bridge writes and confirm it is decoded without replacement characters.
- [ ] Repeat with CP949 and confirm a Terminal keyword spanning consecutive
  bridge chunks still matches.
- [ ] Change RX encoding and Terminal/HEX display modes; confirm forwarded bytes remain unchanged.
- [ ] Pause rendering and minimize the app while sending sustained virtual-to-device traffic; confirm the device continues receiving without bridge transport drops.
- [ ] If the UI-only bridge log queue is forced to overflow, confirm only its UI drop counter increases while transport byte counts continue.
- [ ] While Bridge is OFF, confirm the normal awaited RX pipeline remains active and footer drop counts stay at zero.
- [ ] While Bridge is ON, force parser/UI overload and confirm raw bridge traffic continues while only the bridge-priority parser/log drop counter increases.
- [ ] Stop the external program temporarily and confirm bridge backlog/drop/error counters remain bounded and visible.
- [ ] While bridge traffic is active, request one manual TX and confirm the UI
  shows `TX waiting for bridge idle`; verify a second button/Enter/saved/history/
  shortcut request is rejected as Busy and the first payload is sent once after
  25 ms of global bridge idle.
- [ ] Confirm Send, Enter, Quick command, saved shortcut, and history resend are
  disabled immediately on the Waiting transition and remain disabled through
  Sending, without waiting for the footer status refresh timer.
- [ ] Split one HEX rule pattern at every byte boundary across consecutive
  writes on COM5 and confirm the TxOnly/Both rule fires once for each logical
  idle group while the bytes reaching the device remain unchanged.
- [ ] Send HEX continuously without a 25 ms idle gap and confirm display records
  appear within 50 ms or 256 bytes, each `RawBytes` payload is at most 256 bytes,
  and a pattern split across two display records does not match Event, Highlight,
  or View filter rules.
- [ ] Stop the bridge while a COM5 read is completing and confirm normal stop
  does not increase virtual-to-device drop/overflow counters or set a fault.
- [ ] Confirm command sequences cannot start while Bridge is ON.
- [ ] Saturate the device-to-virtual queue and confirm the bridge alone stops
  with `Bridge stopped: virtual COM consumer too slow`, while the device COM and
  normal RX remain connected.
- [ ] Force a bridge warning or failure and confirm the footer health tooltip
  keeps the reason visible and copied status includes it.
- [ ] Stop the bridge and confirm the `BRIDGE ON` indicators disappear.
- [ ] Disconnect the device and confirm the bridge stops before the device port closes.

## Xterm Log View

- [ ] Confirm xterm log lines append smoothly.
- [ ] Resize the app and confirm the xterm area fits the available space.
- [ ] In Settings > Font, change the xterm font family and size; confirm both
  apply immediately without clearing or duplicating visible log lines.
- [ ] Save the profile, restart or reload it, and confirm the selected xterm font
  family and size are restored.
- [ ] Drag-select multiple xterm lines.
- [ ] Press Ctrl+C.
- [ ] Paste into Notepad and confirm selected text copied.
- [ ] Under sustained traffic, click Pause View and confirm it briefly shows
  `Pausing View`, then `Resume Live`. Once `Resume Live` appears, confirm the
  terminal no longer moves.
- [ ] With `Keep saving file log` checked, confirm RX/file/event counters continue
  increasing, UI pending stays bounded, PS increases, and Drop UI remains unchanged.
- [ ] Leave the view paused for more than 10,000 incoming records and confirm the
  serial log file continues growing beyond that point while Q U stays bounded.
- [ ] Click Resume Live and confirm paused records are not replayed, a resume
  boundary containing the pause PS count is shown in gray, and only newly
  received records append afterward.
- [ ] While paused, change a display format/filter if practical, minimize and
  restore, then resume; confirm pause-period records never reappear after a full
  re-render or restore.
- [ ] Clear `Keep saving file log`, repeat the pause, and confirm RX/events continue
  while pause-period records are omitted from the file and the gap is marked.
- [ ] Click Clear and confirm only the visible terminal clears.
- [ ] Minimize under sustained traffic, restore with at least 5,000 one-line
  records pending, and click Clear while the restore overlay is visible. Confirm
  the overlay closes promptly, no pre-Clear lines reappear, and RX lines received
  after the click appear after the cleared boundary without requiring a full
  re-render.
- [ ] Repeat while deliberately delaying restore cancellation if using a debug
  hook, then minimize again before Clear completes. Confirm the latest Clear owns
  the barrier, no pre-Clear live batch reappears, and live appends resume exactly
  once after the window is restored.

## TX Manual Command

- [ ] Connect to `MOCK` or hardware.
- [ ] Enter a command in TX.
- [ ] Choose TX ending.
- [ ] Click Send.
- [ ] Confirm a TX line appears in xterm.
- [ ] Confirm the TX line appears in the serial log file.
- [ ] Confirm Send is disabled or fails gracefully while disconnected.

## Saved Commands

- [ ] Confirm the toolbar reads `Quick CMD  +  |  ps  meminfo …`, with no Saved dropdown or standalone Edit/Del buttons.
- [ ] Click `+` and add a saved command named `reboot` with command text `reboot`.
- [ ] Confirm a compact `reboot` quick-send button appears.
- [ ] Connect and click `reboot`.
- [ ] Confirm TX logging works.
- [ ] Right-click `reboot`, choose Edit, and confirm the chip updates after saving.
- [ ] Send one command, then right-click a different command and confirm Edit/Delete targets the right-clicked command.
- [ ] Right-click `reboot`, choose Delete, and confirm it disappears after confirmation; Cancel leaves it intact.
- [ ] Confirm add/edit/delete works while disconnected.
- [ ] With enough commands to overflow, confirm only the command list scrolls and `Quick CMD` / `+` stay visible.

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

## Log File Name And Runs

- [ ] Start the app and confirm the header shows red-orange `LOG OFF` even if the
  previously saved profile had logging enabled.
- [ ] Connect and confirm no serial log is written until `LOG ON` is pressed.
- [ ] Enter log file name `inverter test.log` and confirm the adjacent `Current:` value shows
  exactly `inverter test.log`.
- [ ] Press `LOG ON` and confirm only `inverter test.log` is created, with no
  date, time, `_serial`, or extra extension added.
- [ ] Confirm the file-name field is disabled while LOG is ON and enabled again
  after `LOG OFF`.
- [ ] With `inverter test.log` still present, press `LOG ON` and confirm it is
  refused rather than appended to or overwritten.
- [ ] Clear the file name, press `LOG ON`, and confirm a new automatic
  `yyyy-MM-dd_HHmmss_serial.log` file is created.
- [ ] Confirm `LOG ON` is light green and `LOG OFF` is red-orange in both the
  header and Settings.

## Event Detection And Original Logs

- [ ] Connect to `MOCK`.
- [ ] Wait for WARN/ERROR/FAULT events.
- [ ] Open Events tab and confirm events append incrementally.
- [ ] Select an event.
- [ ] Confirm the Context tab is absent; Events and rule notification controls remain.
- [ ] Double-click a retained event and confirm its original log is selected in
  the main terminal, with surrounding lines available for reading.
- [ ] Continue receiving logs and confirm the jumped position stays visible until
  Auto Scroll is explicitly enabled again.
- [ ] Double-click a filtered or expired event and confirm a clear status message
  appears without jumping to a different log with the same message.
- [ ] Confirm event detection and original-log navigation work with Log Save OFF.
- [ ] Confirm no `*_events.log` file is created.

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

- [ ] Open Add/Edit log rule and confirm all options, including `Case sensitive`,
  are visible. Check the two-column fields and notification row in dark/light
  themes, at 100%/150% scaling, and with a short window (content should scroll).
- [ ] Confirm all eight checkboxes and their labels share the same vertical
  center in checked, unchecked, and disabled states.
- [ ] Read the explanation below RX trigger sequence: `(none)` disables it;
  with no executable sequence it points to Sequences; with TxOnly it explains
  that RxOnly or Both is required. Select a sequence and match RX with Event
  unchecked: commands should run once, with additional triggers ignored while busy.
- [ ] Set Priority to `100000` and cooldown to `86400`, save, reopen, and reload
  the profile. Both values must remain unchanged. Invalid Priority or cooldown
  outside `5`–`86400` must keep the editor open with an inline error.
- [ ] Switch the editor's Mode to HEX: `Case sensitive` becomes unavailable;
  switch back to Terminal and confirm its previous check state is retained.
- [ ] Open Rules tab.
- [ ] Confirm the rule editor and rule list label the selector as `Mode`, with
  `Terminal` and `HEX` choices (not `Match = Text/HEX`).
- [ ] In Terminal mode, enable one Terminal rule and one HEX rule whose keywords
  both represent the same incoming bytes; confirm only the Terminal rule creates
  events/highlights.
- [ ] Switch to HEX mode without changing either rule's Enabled state and confirm
  only the HEX rule creates events/highlights.
- [ ] Mark both rules as filters and confirm the Filter flyout lists only rules
  for the current mode.
- [ ] Receive matching and non-matching lines before opening Filter. Check both
  rules, select `Apply`, and confirm existing and future lines matching either
  rule are visible while other lines are hidden. Apply `ALL` and confirm hidden
  retained lines return in their original order without Clear.
- [ ] With a large retained history and live RX, rapidly switch filters, change
  capacity/format, or Clear during filtering. Confirm the latest filter wins,
  RX/file logging continue, memory stays bounded, and removed lines never return.
- [ ] Reopen the Filter flyout, change the checks, close it without applying, and
  confirm the active filter selection is unchanged.
- [ ] Select `ALL` and confirm the individual rules are cleared; switching modes
  must fall back to `ALL` if none of the selected filters belong to the new mode.
- [ ] Toggle WARN highlight off.
- [ ] Confirm future WARN lines no longer use the WARN highlight color.
- [ ] Change ERROR highlight color.
- [ ] Confirm future ERROR lines use the new color.
- [ ] Add a highlight rule for `RESET`.
- [ ] Confirm the rule persists after Save Profile and restart.
- [ ] Receive a Terminal line, switch the retained RX view to HEX, and confirm
  HEX highlight/filter rules match its raw bytes in the rebuilt view.

## Search

- [ ] Hide timestamps and RX/TX prefixes, receive `SYS READY`, `RX < READY`,
  `[12:34:56] READY`, and a payload with leading spaces. Search each entire
  payload and confirm neither the match nor its leading text is lost.
- [ ] After searching, change the query/options or timestamp/direction display.
  Press F3/F4 before refreshing: confirm the old result selection does not move.
- [ ] Keep receiving while a large search runs and confirm its result snapshot
  is marked stale when newer logs were excluded.
- [ ] Search more than 1,000 matching lines. Start with Shift+Enter and cross
  page boundaries with F3/F4: confirm the list page and selected row follow the
  original-log position. Press Enter on a selected result to jump to it.
- [ ] Search a combining accent, Korean text, emoji, and tab-containing text;
  confirm selection covers the intended terminal cells and nearby logs remain visible.

- [ ] Search for `WARN`.
- [ ] Confirm match count updates.
- [ ] Receive `RX < \tREADY` with a real tab, search for `READY`, and confirm
  the rendered word is selected even though the tab expands to multiple cells.
- [ ] Search for a term that includes a literal tab and confirm the expanded
  tab cells on the target occurrence are selected.
- [ ] Receive `\t\tREADY`, search for a literal tab, and confirm the first and
  second tab remain separate results and select separate tab-stop ranges.
- [ ] Size the terminal so its cursor reaches the right margin, receive
  `\tREADY` (and then `\t\tREADY`) from that position, and confirm searching
  for `READY` selects the word even though the preceding tabs render no cells.
- [ ] Size the terminal so a CJK character or emoji soft-wraps from the final
  column, place a real tab and `READY` after it, and confirm `READY` jumps to
  the correct rendered cells rather than counting the wide-wrap placeholder
  as another tab.
- [ ] With a tab before the payload text, case-insensitively search Unicode
  pairs such as `ſ`/`S`, `K`/`K`, and `ß`/`ẞ`; confirm result count and jumps
  follow .NET `OrdinalIgnoreCase` rather than JavaScript case folding.
- [ ] Receive a payload ending in `READY ` and confirm a search including the
  trailing space both counts and jumps to the match.
- [ ] Send `open_rx_mq() ...  rx_mq`, search for `rx_mq`, and confirm both
  occurrences on the same logical line are counted and selected in order.
- [ ] Search for ` rx_mq` (including the leading space) and confirm only the
  right-hand occurrence is counted; save/reload the profile and confirm the
  leading space is preserved.
- [ ] Click Next and Prev.
- [ ] Confirm the magnifying-glass Search button sits immediately left of Prev,
  within the existing search field area, without increasing toolbar padding or gaps.
  Click it and compare with Enter: both refresh the visible-log snapshot using the
  current options, select the next match, and add the query to search history.
- [ ] Confirm xterm jumps/selects matches.
- [ ] Append additional matching logs, then use Next/Prev and F3/Shift+F3/F4;
  confirm the total match count remains the last Enter-search snapshot.
- [ ] With Auto Scroll OFF and timestamp/direction prefixes hidden, receive an
  unterminated tab-only RX segment. Search and navigate older matches; confirm
  the selected text belongs to the requested log line, including repeated text.
- [ ] While RX has an unfinished line, change a display setting that redraws
  the logs. Search the first retained log; confirm its text and match offsets
  are unchanged. Clear during partial RX, then confirm new logs start cleanly.
- [ ] Press Enter in the search box and confirm the total match count updates to
  include the newly appended matches.
- [ ] Click the xterm log and press Enter; confirm Auto Scroll toggles once.
- [ ] Hold Enter while xterm is focused; confirm key repeat does not toggle it repeatedly.
- [ ] Focus the TX input and press Enter; confirm the command sends without toggling Auto Scroll.
- [ ] Toggle Case and confirm behavior changes.
- [ ] After a completed search, edit the search text or toggle `Aa`, `|ab|`, or
  `.*`; confirm the existing result rows remain visible as stale until Enter or
  Refresh runs the new search. Confirm F3, page navigation, and result jumps are
  blocked while those rows are stale, with a refresh-first message for a result
  jump attempt. Clear the input completely and confirm either Enter or Refresh
  clears the retained rows.
- [ ] Start a search on a large snapshot, edit the search text before it
  finishes, and confirm the old result rows, page count, and selected match stay
  together; confirm no canceled-search xterm selection occurs.
- [ ] With valid regex results visible, enter an invalid regex such as `[` and
  press Enter; confirm the prior rows and snapshot remain visible as stale while
  the readable regex error is shown. Correct the pattern and refresh to replace
  them normally.
- [ ] Toggle Whole Word and confirm `error` matches `error-error` but not
  `errorCode` or `preerror`.
- [ ] With Whole Word enabled, search `a-a` in `xa-a-a`: confirm the trailing
  `a-a` is found with Regex both off and on.
- [ ] Toggle Regex, search for `id=\d+`, and confirm variable-length matches
  are counted, highlighted, and selected at the correct xterm range.
- [ ] Combine Case, Whole Word, and Regex and confirm all three constraints
  apply to the same result snapshot.
- [ ] Enter an invalid regex such as `[` and confirm a readable search error is
  shown while RX and UI rendering continue normally.
- [ ] Search with a zero-length-only regex such as `^` or `(?=a)` and confirm it
  reports zero matches rather than creating an unselectable phantom result;
  confirm a consuming anchored regex such as `^WARN` still works.
- [ ] In the search box, press Alt+C, Alt+W, and Alt+R and confirm the matching
  option toggles once without changing the search text.
- [ ] Search for a timestamp-only value or direction marker such as `RX <` and
  confirm it does not match unless the same text exists in the payload itself.
- [ ] With a narrow log viewport, create long wrapped matching lines, leave the
  results stale while newer matches append, and confirm an old result jumps to
  the same logical line or reports no hit after that line leaves scrollback.
- [ ] Confirm logs continue appending during search.
- [ ] With 200,000 retained lines containing two matches each, confirm the
  total is 400,000, each matching line appears once, only 1,000 result rows
  are materialized per page, search can be canceled by changing the term, and
  the UI remains responsive.

## Search Results Tab

- [ ] Open Search tab.
- [ ] Confirm result rows are compact and stable.
- [ ] Confirm repeated matches on one line appear as one row labeled `×N` and
  double-clicking the row jumps to the first occurrence on that line.
- [ ] With more than 1,000 matching lines, confirm `Prev` moves toward older
  pages and `Next` moves toward newer pages while each page stays at or below
  1,000 rows.
- [ ] After new logs mark a multi-page result snapshot stale, move between its
  existing pages and confirm the stale indicator remains until a refresh builds
  a new snapshot.
- [ ] On the newest results page, append more matching logs and click `Next`;
  confirm the search refreshes and moves to the newest available page.
- [ ] Confirm status appears in the toolbar, not as a result row.
- [ ] Confirm there is no Auto refresh control and Search results update only after Refresh.
- [ ] Click Refresh and confirm results rebuild.
- [ ] Double-click a result and confirm xterm jumps/selects it.
- [ ] Confirm stale/manual status is clear while logs append.

## Settings Validation

- [ ] While connected with LOG OFF, change the save folder by typing or Browse.
  Confirm no reconnect is required, start logging and verify the file is in the
  new folder. While LOG ON, both folder editors must be disabled.
- [ ] Enter a slow or unavailable network log folder. The window must remain
  responsive, validation must report failure, and the last valid folder must
  remain selected. Repeated retries must not accumulate filesystem workers.
- [ ] Change the log folder or file name with LOG OFF. Confirm the apply status
  says the next logging start and its pending count clears after logging starts.
  Load or reset a profile and confirm earlier pending settings are cleared.
- [ ] In HEX mode, the TX ending selector must be disabled both in the toolbar
  and Settings. Return to Terminal and confirm the previous ending is retained.
- [ ] Toggle timestamp visibility with retained logs and no incoming RX. Existing
  timestamps must disappear/reappear immediately; the tooltip must describe this.
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

- [ ] Without opening the Log tab, find **Export logs...** / **로그 내보내기...** immediately
  to the right of **Start Logging / Stop Logging**. Confirm its icon and text
  remain visible when the inspector is collapsed and the window is narrow.
  Verify it uses the same save action in English and Korean, including while
  live file logging is on. Receiving and the log view must stay usable while
  the non-modal progress panel is shown.

- [ ] With LOG OFF, receive MOCK data and use **Export logs...** in the
  Log tab and the LOG button's right-click menu. Verify the UTF-8 file contains
  earlier RX, TX, MARK and system records with timestamps. Repeat in Korean UI.
- [ ] Hide records with a view filter, then save retained logs. Verify hidden
  records are included. Repeat after retention rollover/Clear and confirm deleted
  history is not exported. Cancel the save dialog and verify no error is shown.
- [ ] While receiving, press Export logs and leave the save dialog open as new
  records arrive. Confirm the suggested name contains the first retained record's
  timestamp and the button-click time (both local dates/times, to milliseconds).
  Confirm the output stops at the click-time snapshot, excluding records received
  while the dialog was open. Repeat across midnight and after retention rollover;
  the first timestamp must refer to retained history, not discarded session history.
- [ ] While receiving and saving a live log, export history to a separate file.
  Verify receiving/live logging continue and the export is a stable snapshot.
  Try the active log path and a read-only destination; verify a clear error,
  intact existing files, and that another export can be attempted.
- [ ] During a slow export, verify the progress panel stays inside the upper-right
  log viewport and leaves TX input/send and inspector controls available. Cancel
  a new destination and confirm neither an empty output nor a temporary file is
  left behind. Retry and verify the completed output and live logger keep their
  correct contents. See [GUI validation evidence](log-export-ux-validation.md).
- [ ] Export a large history (up to 1,000,000 records) while MOCK stress is running.
  Verify the progress panel shows percentage and written/total records, the terminal
  and RX counters advance, and tabs/controls remain usable. Completion must not show
  100% before the destination is replaced. Repeat in Korean and English.
- [ ] Cancel a running history export over an existing file. Verify the panel shows
  cancellation until cleanup finishes, the old file is preserved, no temporary file
  remains, and another export succeeds. Close the app during export and verify the
  normal RX/live-log shutdown still completes promptly.

- [ ] Connect to `MOCK`.
- [ ] Wait until serial log file is created.
- [ ] Generate an event and confirm it remains available in Events; double-click
  to inspect its original log while it remains in the visible buffer.
- [ ] Open the Log tab.
- [ ] Open current serial log.
- [ ] Open logs folder.
- [ ] Copy serial log path and paste into Notepad.
- [ ] Confirm no event-log open/copy action is shown.
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

## Physical COM Packet And Timeout Stress

- [ ] Confirm the `COM4 <-> COM5` com0com pair is free.
- [ ] In the sender instance, connect to MOCK, bridge to `COM4`, select
  `Visual HEX 3-5 ms`, and start stress.
- [ ] In the receiver instance, connect to `COM5` at 460800 baud in HEX mode and
  apply a 15 ms HEX timeout; confirm it sees
  2-6 packets per group with a new group after each long gap.
- [ ] Confirm every correct logical line starts `AA 55 F1`, stays on one group
  number, and ends with its `F3` packet; treat lines starting `F2/F3` as splits
  and an `F3 -> next F1` pair on one line as a merge.
- [ ] For the standalone tool test, close both instances, reopen the receiver on
  `COM4`, and run the stress tool on `COM5` in timeout mode using the command in
  `docs/com0com_stress_testing.md`.
- [ ] Confirm 3-5 ms packets stay in one HEX group until each 32-40 ms gap.
- [ ] Confirm the next packet after the long gap starts a new HEX group.
- [ ] Confirm RX/file counters rise, UI remains responsive, and serial and
  connection error counters stay at zero.
- [ ] Repeat in load mode for at least two minutes and verify the tool's final
  actual-gap and throughput summary.
- [ ] Close Serial Monitor and run the opt-in automated native-boundary test.

## Health And Footer Status

- [ ] During sustained mock event bursts, confirm the event list updates without
  entry/exit fades, retains the configured visible limit, and follows the latest
  event only when auto-scroll is enabled and the pointer is outside the list.
- [ ] Select a retained event during a burst and verify its selection stays on
  that event; verify safe selection clearing when it leaves the buffer.
- [ ] Copy footer status during a burst and confirm `Event UI` reports pending
  items, batch count, maximum batch size, queue drops, and last/maximum synchronous
  batch time. These times exclude deferred XAML layout, paint, and scrolling.
- [ ] Overload the event UI queue in mock mode and verify a nonzero queue-drop
  count appears in the footer UI-drop total and `UI event list updates dropped`
  appears in health details. Normal visible-history eviction must not produce
  this warning. Check event detection and file logging separately for loss.

- [ ] Confirm bottom status shows `HEALTH OK` during a clean mock session.
- [ ] Confirm hovering over the footer shows the current health reasons.
- [ ] Right-click the footer, copy status, and paste it into Notepad.
- [ ] If practical, force a warning/error counter and confirm health changes to
  `WARNING` or `ERROR`.
- [ ] Trigger a recoverable validation error and confirm it does not leave Health
  permanently at `ERROR`.
- [ ] Recover or restart a failed background service and confirm its active
  background error clears from the footer reason list.
- [ ] Force two different observer loops to fail and confirm both reasons remain
  independently visible; a service status change must not clear either reason.
- [ ] Confirm an observer reason clears only after that observer task is actually
  created again. Bridge observer failures should remain until app restart.
- [ ] Accumulate more than five health reasons and confirm the tooltip shows five
  plus a remaining count while copied status includes every reason.
- [ ] Confirm stale profile or search-result states are not treated as health
  errors.

## Time/position navigation and thin time rail

- [ ] Confirm the terminal has a thin vertical time rail at its left (about two characters, at most 24 px), retains its full available height, and shows no horizontal activity/peak graph or Navigate inspector tab.
- [ ] With an empty buffer, open Ctrl+G and verify helpful empty-state validation with disabled time fields/Go. Receive logs, close/reopen and confirm the range becomes available. Ctrl+G and the log context menu must open the same native dialog; Escape/Cancel must restore focus without moving the log.
- [ ] Open Ctrl+G at a historical position and confirm the corresponding time is selected with focus on Minutes. Change Hours/Minutes/Seconds by mouse wheel and native keyboard arrows. Open a dropdown and verify its list keeps normal wheel scrolling. The dialog and button positions must remain stable.
- [ ] Receive 200 logs with an identical millisecond timestamp, open Ctrl+G after that timestamp, and select a later idle time: Go must target log 200 by its frozen ID. Receive more logs while the dialog is open and repeat; it must still target the original log 200. If that ID is evicted, show an expired-position message without jumping to another log. An ordinary request exactly at the shared timestamp must still target its first matching log.
- [ ] Minimize/restore or close the window while Ctrl+G metadata is loading: the canceled opening must not show a stale empty dialog. A later Ctrl+G must open a fresh session; an already-open dialog must retain its selection across minimize/restore.
- [ ] Compare RX/file counts with the native dialog closed, open, and while changing selectors. Keep width-reflow timing separate: the current bundled xterm can spend several seconds changing width with one million retained rows; normal height changes must reuse the metadata rings.
- [ ] Open on a single-day range and confirm its date is fixed text. Repeat across midnight, month/year boundaries and leap day; native Year/Month/Day choices must expose only valid dates/times within the captured range.
- [ ] Keep receiving while the dialog is open: the range, selector lists and chosen time must stay fixed. Close/reopen to capture a fresh oldest-retained-to-current-wall-clock range. No polling or text preview should compete with selector changes.
- [ ] During a long RX idle interval, select a time after the last recorded log and press Go; it must land on the last retained log. Choose an interval between recorded timestamps and confirm it lands on the first log at or after that time.
- [ ] In a small window, check the date/time fields, two-line range and error text without raw JSON/Int64/conversion messages. Full trimmed status must remain available by tooltip, and reopening must clear previous errors.
- [ ] Hover the time rail near its top, middle and bottom. Check the actual destination's local date/time tooltip, including across midnight and an interval without logs. Click to jump and confirm the viewport band follows the selected log range.
- [ ] Focus the rail, select a time with arrows/Home/End and press Enter. Escape hides the tooltip. Empty, single-timestamp and system-clock-regressed logs must remain usable without misleading time labels.
- [ ] Turn visible timestamp prefixes off and confirm the rail still shows actual timestamps and navigates by time. With genuinely missing/invalid timestamps, verify it instead shows #line/count and percentage, stays clickable and marks the viewport by retained position. Multiple equal timestamps must allow selecting distinct log positions.
- [ ] Save timestamp display OFF, restart into an empty log view and receive the first logs without changing any setting. The empty startup restore must complete and the rail must become clickable immediately after the first batch. Repeat with timestamp display ON and through later ON/OFF changes; a presentation change must never be required to enable navigation.
- [ ] With no real timestamp metadata, Ctrl+G time fields/Go must be disabled with a helpful message while ordinal rail navigation and available Back history still work. Evict a hovered ordinal target before clicking: show an expired-position message instead of jumping to a different log at that index.
- [ ] Receive new RX after a jump and confirm the chosen viewport stays in place, Auto Scroll is off, RX/file logs continue, and view pause is unchanged.
- [ ] Use Back from the Ctrl+G dialog repeatedly, including a wrapped log offset before and after column resize. After retention eviction, an expired Back anchor must report unavailable rather than jump elsewhere.
- [ ] With one million retained lines, drag/resize the window, use the rail near its middle and open the time selector. No histogram requests, repeated whole-buffer scans, unbounded history, or frozen receive controls.
- [ ] Verify Tab/Shift+Tab, arrow keys and Enter operate native date/time fields without toggling terminal Auto Scroll. Escape must close the native dialog or its open dropdown. No HTML popup or browser-native controls should appear.
- [ ] Verify Korean/English, light/dark/high contrast, narrow widths, clear/mode change, full screen restore, and a system clock regression.

## AI Plugin / CLI / MCP

- [ ] Confirm Terminal/HEX is fixed to the left of Port, including when the connection toolbar is scrolled. Open each mode dropdown and check the short Terminal/HEX item tooltips in Korean/English.
- [ ] Use CLI `mode`, `mode --set hex`, and `mode --set terminal`, or MCP `serial_set_mode`. Check status.mode, both GUI selectors and active rule mode; mode changes clear visible logs/search without deleting saved logs. A mode change sends no bytes and does not open/close COM. Explicit CLI/MCP TX formatting remains independent.
- [ ] During AI capture, GUI TX, sequences, bridge startup/activity and reconnect, mode mutations must be busy and the GUI mode selectors disabled. After completion/cancellation, mode selectors must recover.
- [ ] Confirm the old MCP menu is named Plugin. Check quick start, client installation/update, usage examples and troubleshooting in Korean/English, narrow panels and light/dark themes.
- [ ] In Plugin quick start and About, confirm GitHub is the primary installation source and `docs/agent_plugins.md` is the guide. ZIP/local installation does not appear in the quick-start flow.
- [ ] Install from `https://github.com/Establers/custom-serial-monitor.git` independently in Codex desktop/CLI, Claude Code and Copilot CLI. Confirm the plugin skill and MCP tool schemas are available in a new session. Verify Copilot VS Code with agent plugins enabled; do not treat a Codex IDE MCP fallback as a plugin installation.
- [ ] Update the published Git plugin through each host. Confirm marketplace listing refresh versus installed-plugin update follows the selected host.
- [ ] Upgrade the GUI in place and reconnect MCP. Confirm the Git-installed plugin remains managed by its Git source. Separately test `<app>\agent-plugin` as a development/offline fallback and apply local changes through the host's reload/restart.
- [ ] Install to a path containing spaces/Korean and to another drive. Test launcher discovery from the installed registry and from a nearby portable app; test `SERIAL_MONITOR_CLI` with AI launched from that actual shell.
- [ ] Run `serialctl describe --json` and MCP `serial_get_capabilities` without a running GUI. Confirm supported tools/versions are returned with no COM access or TX. Test missing/old CLI diagnostics without contaminating MCP stdout.
- [ ] Keep an old standalone skill and manual server registration while migrating. Confirm setup does not delete them; intentionally remove only superseded duplicates after read-only plugin verification.
- [ ] Run the new GUI, then `serialctl instances`; select `--instance` when two GUIs are running.
- [ ] Connect through the GUI, then `serialctl request --port COM7 --text "status" --duration-ms 2000`.
  Confirm actual RX appears in both the GUI and the capture; interleaved output is preserved without matching.
- [ ] Pause/filter the GUI and switch Terminal/HEX modes; captures must continue independently.
- [ ] Capture more than 16 KiB. Follow every `page.nextOffset` until `hasMore=false` and compare hex with `.bin`.
- [ ] During a capture, GUI TX/sequence/bridge start and a second AI capture must be busy; status remains readable.
- [ ] Use `AI 중지`, CLI cancel, MCP cancellation, client exit, and physical disconnect; control must be released.
- [ ] Force disk/queue failure and verify explicit capture failure or rxGapDetected without stopping serial RX.
- [ ] Register `serialctl mcp` in an AI CLI and let it read saved commands, execute one, inspect all RX, and select the next command.
- [ ] While bridge startup is waiting for its virtual port to open, AI control must return busy; startup failure/cancellation must release control.
- [ ] During AI connect/port refresh, GUI connect and connection-setting controls must be disabled. Verify success only for the requested port and baud.
- [ ] Cancel AI connect during port enumeration, lifecycle-gate wait and port opening. No delayed connection may start; after a successful connect, closing the caller must leave the session connected.
- [ ] Keep an old capture's bin/txt/json open while starting capture 33. The new capture must succeed, every file of the pinned capture must remain readable, and another old capture must be evicted.
