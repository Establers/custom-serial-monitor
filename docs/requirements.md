# Project: WinUI 3 Serial Monitor

## Goal

Build a Windows desktop serial monitoring application using C# and WinUI 3.

This application is intended for long-running embedded device debugging.  
Stability is more important than visual effects.

The app must support 115200 bps serial monitoring, long-running log capture, command transmission, keyword/event detection, and file logging.

## Tech Stack

- Language: C#
- UI: WinUI 3
- Platform: Windows
- Target: .NET 8 or later if possible
- Architecture: MVVM-friendly structure
- Serial I/O: use a dedicated service layer
- Logging: asynchronous file writer
- UI update: batched update, not per-byte or per-line direct update

## Priority: Must Have

### Serial RX Stability

- The application must support 115200 bps serial monitoring reliably on Windows.
- Serial reception must not be blocked by UI rendering or file writing.
- Serial RX, line parsing, file logging, event detection, and UI rendering must be separated.
- The application must run for at least 72 hours without crashing or noticeably slowing down.
- The main log view must keep only the most recent N lines or N MB.
- Full logs must be saved to files.
- Example:
  - UI log view: latest 50,000 lines by default, configurable up to 500,000
  - Full text log: saved completely
  - Event view: latest 5,000 events in the bounded UI buffer

### Timestamp

- Each received log line must have a PC-side timestamp.
- Timestamp format:
  - `YYYY-MM-DD HH:mm:ss.SSS`

### TX / Command Transmission

- The application must support writing shell commands to the serial port.
- Frequently used TX commands must be saved with a name.
- Saved commands must be sendable via one-click buttons.
- Saved commands should support keyboard shortcuts later.

### Event Detection

- The user must be able to register target keywords.
- If a keyword is detected in received serial logs:
  - show detected time
  - show matched keyword
  - show original message
  - display it in a separate event view
  - keep it in the bounded event/context UI without creating a separate event file

### Copy

- The user must be able to drag-select text in the serial monitoring area and copy it.

### Line Endings

- RX line ending mode must support:
  - CR
  - LF
  - CRLF
  - Auto
- TX line ending mode must support:
  - None
  - CR
  - LF
  - CRLF

### Disconnection / Reconnection

- If the serial port is disconnected, the application must not crash.
- The application must show disconnected status.
- The application must support manual reconnect.
- The application should optionally auto-reconnect when the same COM port appears again.

### Log Rotation

- Text log files must support daily rotation and/or size-based rotation.
- Example:
  - `2026-05-23_091530_serial.log`
  - `boot_test.log` (when entered explicitly)
- Logging must start OFF on every app launch and profile load.
- Every explicit LOG ON must create a new serial log rather than append to an
  earlier run. A custom file name is used exactly and must not already exist.
- The custom file name is editable only while LOG is OFF, and the configured
  name must remain visible in Settings.

### Encoding / Raw Data

- RX decoding must support:
  - ASCII
  - UTF-8
  - CP949
  - HEX view
- Invalid bytes must not crash the program.
- HEX mode must preserve received bytes as byte-exact hexadecimal text in the
  serial log. A separate raw binary log file is out of scope.

## Priority: High

- Multiple event detection conditions must be supported.
- For keyword events, capture context for the bounded UI viewer and copy action:
  - N lines before the event
  - event line
  - N lines after the event
- Search current visible logs:
  - find next
  - find previous
  - case-sensitive option
- Pause must pause only UI rendering.
  - RX must continue.
  - File logging must continue.
  - Event detection must continue.
- Clear operations must be separated:
  - clear screen log
  - clear log file
  - clear event list

## Priority: Medium

- Profiles must be saveable/loadable:
  - COM port
  - baudrate
  - RX line ending
  - TX line ending
  - save path
  - keywords
  - TX command list
- Keyword highlight:
  - ERROR: red
  - WARN: yellow
  - INFO: normal
  - TX: blue
- TX and RX must be distinguishable.
  - `[2026-05-23 16:12:01.120] TX > status`
  - `[2026-05-23 16:12:01.181] RX < system ok`
- User-entered TX command history must be supported.
- Auto-scroll option must be supported.
- If the user is manually scrolling old logs, auto-scroll should pause temporarily.

## Priority: Low

- Real-time filter by keyword or regex.
- Sound, popup, or tray notification when a keyword is detected.

## Stability Criteria

- The application must run for 72+ hours with continuous 115200 bps reception.
- UI latency must not noticeably increase after 72 hours.
- Memory usage must not grow without bound.
- File logging must not cause RX data loss.
- Disconnect/reconnect must not crash the application.

## Non-Negotiable Architecture Rules

- Do not update UI controls directly from the serial receive callback.
- Do not write log files directly from the serial receive callback.
- Do not append unlimited logs to the UI control.
- Use a bounded in-memory buffer for the visible log.
- Use background tasks/channels/queues for RX, parsing, logging, and event detection.
- Use batched UI updates, for example every 50 ms to 200 ms.
- All exceptions in background tasks must be handled and reported to the UI status area.
