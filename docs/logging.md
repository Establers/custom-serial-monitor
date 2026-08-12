# Logging Behavior

The app writes one asynchronous plain-text serial log stream. Event detection is
independent from file logging and does not create an event log file.

## Log Save ON/OFF

- Every app start and profile load resets Log Save to OFF. The value is never
  restored from a saved profile; the user must explicitly press LOG ON.
- Turning Log Save ON while connected starts the background file writer before
  new lines are accepted for disk logging. Until `StartAsync` reaches `Running`,
  the request is shown as `starting` and records remain terminal/event-only; the
  first record after the atomic ingress activation boundary is offered to the
  writer.
- Turning it ON while disconnected shows `armed` and opens the writer before
  serial RX starts on the next connection.
- Turning Log Save OFF immediately stops new file-log enqueues and gives lines
  already accepted by the bounded writer queue up to 30 seconds to drain and
  flush. If the disk remains unavailable, recovery is canceled, every accepted
  line that was not made durable is counted in `Drop F`, and shutdown continues
  after a bounded 2-second cancellation window. A successfully completed file is
  closed and retained for Open and Copy path actions.
- The LOG switch is the user's requested state, while the writer separately
  reports `Stopped`, `Starting`, `Running`, `Stopping`, or `Faulted`. An
  unexpected fault therefore remains visibly `FAULTED` (or `FAULTED / retrying`)
  in the main status, tooltip, compact status, and health summary instead of
  appearing as `File ON waiting`. If a writer faults after ingress activated,
  file-eligible records remain offered during bounded automatic recovery, are
  rejected without blocking, and are counted in `Drop F`. If the initial start
  faults before activation, records remain terminal/event-only until a retry
  succeeds and are not falsely classified as accepted file ingress.
- `Drop F` and file-error health baselines are captured when the request moves
  from OFF to armed, before any start attempt. A successful initial start or
  automatic restart does not reset those baselines and cannot hide transition
  errors or drops from an already-active ingress session.
- Filesystem open/write/flush/close entry runs on isolated default-scheduler work.
  If an underlying operation remains blocked after cancellation, Stop returns and
  the operation remains in the writer's bounded outstanding set. Lines
  conservatively abandoned at this boundary remain counted as dropped even if an
  in-flight operating-system write later proves to have completed.
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
- Automatic names use `_dup001`, `_dup002`, and so on for same-second collisions.
  Date changes do not split an active log file.
- Optional size rotation keeps the exact name for the first file and adds `_001`,
  `_002`, and so on before its extension for subsequent files. If a rotated
  segment name already exists, `_dup001`, `_dup002`, and so on are added instead
  of stopping file logging. A missing or invalid size threshold defaults to
  10 MB. The Log tab accepts this threshold as a whole number in MB; for example,
  entering `10` rotates at 10 MB.
- File writes are committed in batches of at most 100 lines. Once the first line
  enters a new batch, a monotonic deadline schedules its write and flush within
  two seconds even if no more input arrives. Available channel records are drained
  first; only an empty channel creates one cancellable deadline wait for that
  batch, with no abandoned timer or channel-wait tasks.
- If an open, write, flush, or close makes no progress for 200 ms, the writer abandons that
  file handle, opens the next numbered recovery segment, and retries the complete
  uncommitted batch. A boundary batch can appear in both files if the original
  Windows I/O finishes after the timeout; duplication is preferred to silently
  losing the tail of the log. Directory creation plus entry into file open,
  `WriteAsync`, `FlushAsync`, and `DisposeAsync` are isolated on the default task
  scheduler, so a filesystem or stream method that blocks before returning is
  still covered by the same timeout. A late open result is disposed exactly once.
  If an abandoned `CreateNew` for the first explicitly named file eventually
  returns an empty file, cleanup writes a per-open ownership marker, closes the
  stream, verifies that same marker through a delete-capable Windows handle, and
  deletes that owned file. Once that first open is handed to late cleanup, the
  same recovery incident immediately advances to `_001` rather than mistaking
  its own in-flight `CreateNew` for a pre-existing user file. A replacement active
  file at the same path cannot match the token and is preserved. A file already
  present before the first attempt is still a deterministic collision and is
  never overwritten or retried. Returned mark/delete failures are diagnosed and
  complete that cleanup; a synchronously blocked filesystem call remains in the
  same instance-wide nine-operation ceiling described below.
- If shutdown cancellation wins while an underlying write or flush ignores
  cancellation, the stream is detached without disposing it concurrently. The
  original operation is observed, and that same stream is disposed once only
  after the operation finishes. Stop still returns within its bounded forced
  window and conservatively counts the affected accepted lines as dropped.
- The first recovery open after a write/flush failure is immediate. A failed open
  is retried after a 25 ms backoff. Once the first normal write/flush fails, its
  recovery open, retry write, and retry flush share one monotonic absolute
  500 ms incident deadline and at most 12 failures. Every operation checks the
  deadline before and after entry, and every wait uses the lesser of its 200 ms
  I/O timeout and the incident time remaining. A rotation/name-change batch that
  starts without a stream uses that same budget for both open and flush. A task
  returning success after the deadline is not committed.
- The supported maximum is 921600 baud, with data bits 5/6/7/8, parity
  None/Odd/Even/Mark/Space, and stop bits 1/1.5/2. The shortest supported frame
  is therefore 5N1: one start + five data + no parity + one stop = 7 bits per
  character. Integer wire capacity is `floor(921600 / 7) = 131,657` one-byte LF
  records/s. Capacity planning rounds a further 25% TX/MARK/system reserve up to
  164,572 records/s, then protects the 200 ms first-I/O detection window + 500 ms
  recovery deadline + 100 ms scheduler jitter. Rounding up again requires
  131,658 queue slots. The bounded production queue is 140,000 records, leaving
  8,342 slots of headroom. Tests enqueue all 131,658 records during a stalled
  production-default write and verify zero pre-decision drops.
- Raising the previous 100,000-record ceiling to 140,000 increases the maximum
  retained request count by 40%, but preserves the established 200/500 ms I/O
  and recovery thresholds instead of making ordinary slow storage fault sooner.
  Memory remains count-bounded but is line-size dependent: for 40,000 additional
  approximately 850-character ASCII records, UTF-16 character storage alone is
  roughly 68 MB before object and channel overhead. This is an explicit bounded
  cost for supporting the app's real 5N1 maximum-rate framing; it is not a claim
  of lossless buffering through an indefinite outage.
- This window is incident containment, not indefinite lossless buffering. If the
  source keeps producing through a longer disk outage, the bounded queue can
  fill; excess records and accepted records abandoned when the writer faults are
  explicitly counted in `Drop F`.
- Every isolated open recreates a deleted log directory. Missing directories,
  ordinary I/O failures, sharing violations, and access denial are transient only
  within the finite recovery budget. `PathTooLongException`, invalid paths, an
  explicit existing file, or a directory occupying a candidate file path are
  deterministic and stop immediately. Exhausting all 10,000 bounded duplicate-name
  candidates is deterministic as well and is not retried. A stream that repeatedly
  opens but cannot write is governed by the same attempt/time budget, preventing
  unbounded empty recovery segments.
- Failed stream cleanup runs outside the writer hot path and cleanup lock, so a
  stuck `DisposeAsync` call or returned task cannot block recovery or application
  shutdown. An instance permits eight recoverable detached cleanups. The ninth is
  also reserved and tracked, but immediately stops that writer session. Nine is
  the instance-wide ceiling across stop/start cycles: while all nine remain
  incomplete, restart is rejected without opening another stream. Restart becomes
  available as soon as cleanup capacity is released. The shipping app creates one
  `FileLogWriter` for its single `MainWindow`/`MainViewModel` lifetime, so this
  instance-wide ceiling is also the process-lifetime writer ceiling in the app.
- A close task is never started twice. A fast close fault is reported but the next
  segment may continue. Three consecutive fast close faults permanently stop and
  disable restart on that writer instance, preventing repeated faulting rotations
  from accumulating handles across LOG stop/start cycles. A confirmed successful
  close resets this consecutive-failure count.
- A retryable I/O/recovery-budget fault automatically starts at most one restart
  loop while LOG remains requested and a physical connection or armed reconnect
  session remains valid. Backoff is 0.5, 1, 2, 4, 8, 16, then at most 30 seconds,
  with 12 consecutive attempts until a durable line resets the sequence. Manual
  LOG OFF, disconnect, and shutdown cancel the loop. Deterministic path/name
  errors, the cleanup ceiling, and the close-failure ceiling never auto-restart;
  they remain visibly faulted until the user corrects the cause (and, where safe,
  starts a new run). When a retryable fault leaves an explicitly named first file
  in place, automatic restart preserves it and opens the `_001` (or unique
  `_001_dupNNN`) recovery segment instead of treating the app's own file as a
  user-name collision.
- UTF-8 is encoded directly into one reusable `ArrayPool<byte>` batch buffer;
  there is no byte array per line and no second merged batch allocation. Normal
  batches reuse the rented buffer. A timed-out write holds a reference-counted
  lease, so its array cannot return to the pool or be mutated until the actual
  write task completes.
- File-writer stalls, retries, and queue exhaustion are retained across app
  restarts in `%LOCALAPPDATA%\SerialMonitor\diagnostics\file_writer_incidents.log`
  (with one bounded `.previous` file) for post-incident diagnosis.
- Incident persistence uses one background consumer with a total capacity of 256
  items, including the item currently blocked in the file sink. When full, the
  newest incident is dropped without blocking the caller; the next accepted
  incident includes a summary count. A sink exception drops only that item and
  does not terminate the consumer. Incident file I/O uses a dedicated lock, so
  a stalled incident sink does not block startup, shutdown, or general error
  diagnostics. During view-model disposal, new file-writer incidents are first
  unsubscribed, then the queue receives up to two seconds to drain. A blocked
  sink cannot delay shutdown beyond that window, so final incidents may be lost.
  A later view-model session reuses no incomplete pump and starts a new pump only
  after the previous one has actually finished.
- While a physical serial connection or armed automatic-reconnect monitoring
  session is active, the app registers a Windows
  `SystemRequired` power request so unattended automatic sleep cannot suspend the
  COM port or an in-flight file write. The display may still turn off, and an
  explicit user sleep, shutdown, or restart is still honored. The request is
  retained during unexpected disconnect and long retry delays. Manual disconnect,
  completed reconnect cancellation/disable, and shutdown release it. The final
  physical/logical-session state check runs under the same lock as power-request
  acquisition, making stale release and reconnect acquisition atomic.

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

## Events And Raw Binary

Detected events and before/matched/after context remain in bounded in-memory UI
buffers and can be copied from the Context tab. No `*_events.log` file or event
writer queue exists. Separate raw-binary `.bin` logging is not implemented or
shown as a setting.
