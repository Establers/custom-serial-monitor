# Release Notes Draft

Date: 2026-05-25

## Feature Summary

- WinUI 3 serial monitor for embedded and RTOS debugging.
- Real serial connection support through `RJCP.SerialPortStream`.
- `MOCK` port for local development and repeatable testing.
- WebView2/xterm.js main log view with selectable terminal-style output.
- Batched log rendering with bounded visible log memory.
- Plain-text async serial log writing.
- TX manual commands with configurable line endings.
- Saved TX commands with management UI and shortcuts.
- Command sequences for repeatable multi-command test flows.
- User markers and session start/end markers in the log timeline.
- Optional session names in newly created serial/event log filenames.
- Configurable event rules and highlight rules.
- Event context capture with before/matched/after lines.
- WebView2 event context viewer.
- Visible-buffer search with xterm search/jump integration.
- Search Results tab with manual refresh by default.
- JSON profile save/load/reset.
- Settings validation and apply-behavior hints.
- Mock stress mode and sequence-loss diagnostics.
- Compact health summary and detailed copyable diagnostics.
- Log file quick actions for opening/copying active serial/event log paths.
- In-app Help/Guide tab.

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
- Session filename rotation behavior during real test sessions.
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
   - readable serial and event log files,
   - responsive UI after the run.
