# Manual Regression Test Checklist

Use this checklist before release candidates and after UI/layout changes. Prefer
`MOCK` for repeatable checks, then repeat critical connect/TX/logging checks on
real hardware when available.

## Startup

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

## MOCK Connect And Disconnect

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
- [ ] Confirm event detection/context works with Log Save OFF.
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
- [ ] Check both rules, select `Apply`, and confirm future lines matching either
  rule are visible while non-matching future lines are hidden.
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
- [ ] Confirm xterm jumps/selects matches.
- [ ] Append additional matching logs, then use Next/Prev and F3/Shift+F3;
  confirm the total match count remains the last Enter-search snapshot.
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
- [ ] Generate an event and confirm it remains available in Events/Context.
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
