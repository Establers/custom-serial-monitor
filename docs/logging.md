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
  retains the completed file path for Open and Copy path actions.
- The file writer bounds both queue records (100,000) and queued UTF-8 bytes
  (64 MiB). It flushes every 100 records or by a wall-clock two-second deadline,
  including when no new line arrives.
- `Accepted`, `Durable`, `Uncertain`, and `Abandoned` counts are tracked
  separately. A write/flush failure retries the current uncommitted batch in a
  new segment up to three times; a successful retry can duplicate data because
  the completion state of the failed write is unknowable.
- Directory checks, file open, write, flush, and close each have a 30-second
  timeout. A timed-out operation faults the writer and quarantines its stream
  until the late operation ends; late opens and cleanup operations are tracked by
  the same bounded four-operation ceiling. Naming changes and size rotations use
  the same flush-and-recover path as periodic flushes.
- `Durable` means the writer's flush completed and the data reached the
  operating-system file stream. It does not promise survival of power loss or
  kernel failure; WAL/fsync-on-every-batch is intentionally not part of this
  plain-text logger.
- RX, TX, MARK, and system lines use the same ordered serial log stream.
- Terminal rendering and event detection continue while Log Save is OFF.

## Export Logs

- Use **Export logs...** (Korean: **로그 내보내기...**) immediately to the right of
  **Start Logging / Stop Logging** in the connection toolbar to save records
  received before Log Save was enabled. The same action remains available in
  the Log tab's Actions row and the LOG button's right-click menu.
- Pressing Export logs immediately snapshots all records still retained in memory,
  including records hidden by view filters, before opening the save dialog. Records
  arriving while choosing the name/folder are excluded. The snapshot does not change
  when live reception, Clear, or retention rollover subsequently changes the view.
- The suggested name is `yyyy-MM-dd_HHmmss.fff_to_yyyy-MM-dd_HHmmss.fff_logs.log`:
  the first retained record's local timestamp followed by the button-click time.
  Both dates are included for captures spanning midnight. The first time is the
  start of retained history, which can differ from the session start after Clear
  or retention rollover. Editing the filename does not change the captured range.
- The UTF-8 `.log` / `.txt` file uses the live file logger's timestamp, RX/TX/MARK/SYS
  prefixes and original per-record Terminal/HEX representation. It is independent
  of the current timestamp visibility, filters, and highlight colors.
- Formatting and streaming writes run in the background. Reception and live file
  logging continue; this action does not enable Log Save for future records.
  A non-modal progress panel shows the percentage and written/total record count;
  other controls and terminal rendering remain available. Progress is sampled every
  100 ms using one pending value rather than queuing a UI callback per record.
  Preparation is indeterminate, and the percentage stays below 100 until the final
  flush, close and file replacement finish. Cancel stops the current export; another
  export can be started after cleanup. A completed replacement is reported as saved
  even if Cancel arrives immediately afterward.
  The panel sits at the upper right of the log viewport, leaving TX controls and
  inspector settings available. The destination picker only selects a path; it
  does not create an empty destination before export, so cancellation does not
  leave an empty reserved file behind.
  An active live log file cannot be overwritten. Other selected files are replaced
  only after a successful write; a failed or canceled export removes its temporary
  file and preserves the previous destination. Closing the app cancels the export.
- Only retained records can be saved. Records removed by the configured retention
  limit or Clear, lost to UI overload, or omitted during Pause View are unavailable.
  For complete long-running capture, enable Log Save before receiving data.
- Export and live logging share CPU, memory, and storage bandwidth. A large
  export can compete with live writes on slow storage, and its snapshot keeps
  original records alive while the save dialog is open and until export completes
  or is canceled. See [the concurrent export review](log-history-export-review.md)
  for measurements and remaining limits.
- The measured ~3.3ms snapshot time covers reference copying, not file writing.
  Long payloads are streamed without constructing one whole-file string, but
  completion time and disk usage depend on total encoded bytes. A million
  records averaging 1KB of UTF-8 payload need roughly 1GB; averaging 10KB needs
  roughly 10GB, plus timestamps and direction prefixes. Retention is bounded by
  record count, not total payload bytes. Long records can therefore still use
  substantial memory. The non-modal progress panel and Cancel remain useful.

See [the GUI and wording validation](log-export-ux-validation.md) for actual MOCK
reception, concurrent file logging, progress/cancellation, and screenshot evidence.

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
- Automatic names use `_dup001`, `_dup002`, and so on for same-second collisions.
  Date changes do not split an active log file.
- Optional size rotation keeps the exact name for the first file and adds `_001`,
  `_002`, and so on before its extension for subsequent files. If a rotated
  segment name already exists, `_dup001`, `_dup002`, and so on are added instead
  of stopping file logging. A missing or invalid size threshold defaults to
  10 MB. The Log tab accepts this threshold as a whole number in MB; for example,
  entering `10` rotates at 10 MB.

## Log File Name

The Log tab's file name field is editable only while LOG is OFF. The current
configured value is shown beside it. Invalid Windows file names are rejected
instead of being silently changed. Leave the field empty to use the automatic
timestamp name.

## Terminal And HEX Content

Each log line stores the display representation active when that line was
created. Terminal lines are written as decoded text. HEX lines are written as
byte-exact hexadecimal text. Switching modes does not rewrite earlier disk log
entries.

Bridge HEX logs keep a short on-screen preview but save the full byte sequence
to live files and history exports. Bridge Terminal decoding preserves partial
UTF-8/CP949 characters across idle and 64 KiB display groups. Stopping the bridge
flushes accepted log data and delivers it to file/event queues before cleanup;
an incomplete trailing character uses the normal replacement policy at that
stream end. Bounded-queue saturation is still counted and can drop bridge logs.

## Events And Raw Binary

Detected events and before/matched/after context remain in bounded in-memory UI
buffers and can be copied from the Context tab. No `*_events.log` file or event
writer queue exists. Separate raw-binary `.bin` logging is not implemented or
shown as a setting.
