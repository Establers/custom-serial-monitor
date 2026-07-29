# Release Notes Draft

Date: 2026-07-28

## Released in v1.2.3

- Fixed a minimize/restore regression where roughly 5,000 queued log lines could
  take 30 seconds or longer to become visible again.
- Root cause: the xterm append acknowledgement added for reliability made the
  native host wait until xterm had parsed each host batch. The live path already
  coalesced small batches, but the minimize/restore path retained every original
  UI batch and replayed them sequentially. A long minimized session could
  therefore cause thousands of WebView2 script calls and acknowledgement
  round trips even when the retained log itself was small.
- The v1.2.2 restore-time font synchronization also refreshed xterm's texture
  atlas and layout even when the selected font had not changed, adding avoidable
  work at the same point in the restore sequence.
- Restore now coalesces pending batches to the same 2,000-line/256-KiB bounds as
  live rendering before sending them to xterm. For example, 5,000 one-line host
  batches are reduced to three acknowledged writes while preserving order and
  the final displayed-line sequence.
- Reapplying an unchanged xterm font is now a no-op. Serial RX, parsing, and
  asynchronous disk logging were not blocked by this issue; the regression was
  isolated to rebuilding the visible WebView2/xterm log after restore.
- Clear now supersedes an active delta or full restore. A generation-scoped
  barrier holds RX batches received after the clear boundary until
  `terminal.clear()` completes, then resumes the live append pump so those new
  batches cannot be displayed early and erased by the delayed clear.
- The clear boundary is checked before either minimized-window routing point, so
  a pre-Clear live batch left behind the barrier cannot enter the suspended queue
  and reappear on the next restore if the window is minimized again during Clear.
- Pending JavaScript appends receive a canceled acknowledgement, and full
  restores stop after the current bounded 64-KiB transport chunk instead of
  continuing through all queued chunks.

## Feature Summary

- WinUI 3 serial monitor for embedded and RTOS debugging.
- Real serial connection support through `RJCP.SerialPortStream`.
- `MOCK` port for local development and repeatable testing.
- WebView2/xterm.js main log view with selectable terminal-style output.
- Profile-persisted xterm font controls, including a 10–15 px size range, that
  apply immediately from Settings.
- Batched log rendering with bounded visible log memory.
- Plain-text async serial log writing.
- TX manual commands with configurable line endings.
- Saved TX commands with management UI and shortcuts.
- Command sequences for repeatable multi-command test flows.
- User markers and session start/end markers in the log timeline.
- Exact custom serial log file names configured while LOG is OFF.
- Configurable event rules and highlight rules.
- Event context capture with before/matched/after lines.
- WebView2 event context viewer.
- Visible-buffer search with xterm search/jump integration.
- Occurrence-based search counts and navigates every non-overlapping match,
  including repeated matches on one logical line and terms with leading spaces.
- Asynchronous, cancelable search keeps a compact per-line index and caps the
  Search Results list at 1,000 rows for large retained buffers.
- Search Results tab with manual refresh by default.
- JSON profile save/load/reset.
- Settings validation and apply-behavior hints.
- Mock stress mode and sequence-loss diagnostics.
- Compact health summary and detailed copyable diagnostics.
- Log file quick actions for opening/copying the active serial log path.
- In-app Help/Guide tab.
- Live Terminal/HEX mode and HEX timeout changes without disconnecting the
  active COM port.
- Fixed 40 ms automatic HEX grouping timeout across all baud rates, while
  retaining explicitly saved custom timeout values.
- Inline FTDI troubleshooting guidance without startup or connection popups.
- HEX-only rolling one-minute RX BUSY/IDLE estimate using the applied baud,
  data-bit, parity, and stop-bit framing settings; local TX is excluded. The
  meter also shows the rolling window's highest fixed one-second peak after a
  complete minute and restarts when the visible HEX log is cleared.

## Stability Design Points

- Serial RX, parsing, file logging, event detection, and UI rendering are
  separated by services, queues, channels, and dispatcher-bound UI updates.
- File writing is asynchronous and does not block serial receive or parsing.
- Event detection and context capture are asynchronous and bounded.
- UI rendering uses batched xterm appends rather than per-line UI controls.
- Visible UI buffers are bounded; full logs remain on disk.
- Background service status events are marshaled before updating XAML-bound
  properties.
- Search Results defaults to manual refresh to avoid flicker while logs append.
- Visual highlight colors never change the saved log file format.

## Needs Real Hardware Validation

- 72-hour real COM monitoring with file logging enabled.
- Disconnect/reconnect behavior with multiple USB-UART adapters.
- Sustained high-volume real serial traffic at and above 115200 bps.
- Event detection and file logging under real firmware burst traffic.
- TX command and sequence behavior against real device shells.
- Custom filename creation and collision behavior during real test runs.
- Windows default-app behavior for opening active log files while writing.

## Suggested Next Validation Steps

1. Run the manual regression checklist with `MOCK`.
2. Run mock stress mode at low, medium, and high rates and copy diagnostics.
3. Repeat core connect/TX/log/event checks on one real COM device.
4. Run a multi-hour real serial soak test with file logging and event detection.
5. Run an overnight test and verify:
   - no missing mock or device-side sequence numbers where applicable,
   - no file writer drops,
   - no event detector drops,
   - no xterm append errors,
   - readable serial log files and bounded event context in the UI,
   - responsive UI after the run.
