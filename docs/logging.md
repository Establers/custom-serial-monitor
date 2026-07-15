# Logging Behavior

The app writes one asynchronous plain-text serial log stream. Event detection is
independent from file logging and does not create an event log file.

## Log Save ON/OFF

- Every app start and profile load resets Log Save to OFF. The value is never
  restored from a saved profile; the user must explicitly press LOG ON.
- Turning Log Save ON while connected starts the background file writer before
  new lines are accepted for disk logging.
- Turning it ON while disconnected arms logging for the next connection.
- Turning Log Save OFF immediately stops new file-log enqueues, drains lines
  already accepted by the bounded writer queue, flushes, closes the file, and
  clears the current file path.
- RX, TX, MARK, and system lines use the same ordered serial log stream.
- Terminal rendering and event detection continue while Log Save is OFF.

## Pause View

- Pause View freezes the current terminal snapshot. Records received during the
  pause are not retained in a visual backlog and are not replayed after Resume
  Live; new display output starts with records received after resume.
- The button briefly shows `Pausing View` while display work accepted before the
  click is drained through xterm. Once it shows `Resume Live`, no in-flight
  append from before the boundary remains and the terminal must not move.
- Serial RX, parsing, and event detection continue while the view is paused.
- `Keep saving file log` defaults to ON. When enabled, file logging continues
  independently of the paused view. When disabled, pause-period records are
  intentionally omitted from the file and a resume boundary records the gap.
- Pause omissions are counted separately as `PS` and summarized by the gray
  system boundary shown at Resume Live. `Drop UI` counts only actual UI-overload
  losses. Neither counter applies backpressure to RX, parsing, file logging, or
  event detection.
- A filter/format change, full re-render, minimize/restore, or xterm recovery
  cannot bring pause-period records back because they never enter the retained
  visual buffer.

## File Names And Rotation

- With an empty Log file name: `yyyy-MM-dd_HHmmss_serial.log`
- With a Log file name: the entered name is used exactly. No date, time,
  `_serial`, or extension is added.
- Every LOG ON creates a new file instead of appending. An explicitly named file
  must not already exist; LOG ON fails and preserves the existing file if it does.
- Automatic names use `_dup001`, `_dup002`, and so on for same-second collisions
  and split when the date changes. Explicitly named files do not split at midnight.
- Optional size rotation keeps the exact name for the first file and adds `_001`,
  `_002`, and so on before its extension for subsequent files.

## Log File Name

The Settings > Log file name field is editable only while LOG is OFF. The current
configured value is shown beside it. Invalid Windows file names are rejected
instead of being silently changed. Leave the field empty to use the automatic
timestamp name.

## Terminal And HEX Content

Each log line stores the display representation active when that line was
created. Terminal lines are written as decoded text. HEX lines are written as
byte-exact hexadecimal text. Switching modes does not rewrite earlier disk log
entries.

## Events And Raw Binary

Detected events and before/matched/after context remain in bounded in-memory UI
buffers and can be copied from the Context tab. No `*_events.log` file or event
writer queue exists. Separate raw-binary `.bin` logging is not implemented or
shown as a setting.
