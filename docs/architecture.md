# Architecture

This is the canonical architecture document for Serial Monitor. The application
is optimized for long-running embedded-device debugging, so bounded memory,
backpressure visibility, and recoverable background failures take precedence
over UI polish.

## Runtime data flow

```text
SerialService
  -> bounded byte channel
  -> LogPipeline (framing, decoding, timestamps)
  -> bounded LogLine channel
  -> MainViewModel fan-out
       -> bounded/batched UI renderer -> WebView2/xterm.js
       -> bounded FileLogWriter queue -> serial log files
       -> bounded EventDetector queue -> event/context channels and files

SerialService raw RX notification
  -> non-blocking bounded SerialBridgeService queue (chunks + bytes)
  -> HEX idle-group coalescer / timing-aware virtual writer
  -> app-side virtual COM port

app-side virtual COM RX
  -> SerialBridgeService
  -> physical TX arbiter (bridge priority, idle guard, one manual slot)
  -> SerialService raw TX
```

TX commands and user markers join the flow after parsing as `LogLine` objects.
Only RX data passes through the byte decoder. ANSI highlighting is generated for
the xterm view only; persisted logs remain plain text.

## Component responsibilities

- `SerialService` owns real and mock serial connections, RX byte reads, TX byte
  writes, and serial counters. It never touches UI controls or files.
- `LogPipeline` owns line framing, partial-line state, decoding, timestamps, and
  parser diagnostics. It publishes parsed lines through a bounded channel.
- `FileLogWriter` owns buffered asynchronous writes, rotation, file naming,
  flush, and file-write counters behind a bounded queue.
- `EventDetector` owns rule evaluation, before/after context capture, bounded
  pending captures, and UI event notifications. It does not persist an event log.
- `SerialBridgeService` owns the optional second COM port and forwards raw byte
  chunks in both directions through queues bounded by both chunk and byte count.
  Physical RX uses only a non-blocking offer; overflow faults and stops the
  bridge without disconnecting the device. In HEX mode, the virtual writer uses
  the active HEX idle timeout to coalesce adjacent RX chunks and normally match
  one xterm packet line with one write. A 100 ms maximum latency segments a
  longer continuous group. Terminal mode keeps immediate raw-chunk forwarding.
  The physical writer is the sole scheduler for bridge traffic and one pending
  manual TX.
- `BridgeLogProcessor` owns the optional virtual-to-device RX log representation
  for data received from the virtual port.
  Its bounded background pipeline uses a bridge-dedicated streaming decoder in
  Terminal mode and byte-exact HEX matching data in HEX mode; it never modifies
  the bytes forwarded by `SerialBridgeService`.
- `LogViewModel` owns the bounded visible snapshot and xterm-specific formatting.
  Retention is limited by both line count and a conservative memory proxy. The
  proxy counts source bytes, source/display UTF-16 characters, materialized
  display/search strings, active partial builders, and fixed object/collection
  overhead without claiming to be an exact CLR heap measurement.
- `MainViewModel` coordinates lifecycle and fans parsed lines out to downstream
  components. It currently also contains search, profile application, command,
  diagnostics, and UI-state coordination; this is the main refactoring target.
- `MainWindow` owns WinUI/WebView plumbing, dialogs, shortcuts, and control-level
  interaction. It must not perform serial, parsing, logging, or event business
  logic.

## Concurrency and backpressure

- Serial receive callbacks hand bytes to a channel; they do not block on UI or
  disk work.
- Cross-component queues and channels are bounded. Non-blocking handoffs expose
  drop counters so overload is diagnosable.
- The optional bridge is a byte-exact side path. RX encoding, line framing,
  filtering, and highlighting never transform bridged bytes. HEX display mode
  changes only device-to-virtual write grouping and latency: adjacent chunks
  inside the active HEX idle timeout are emitted in one write. A continuous
  group is emitted after at most 100 ms or 1 MiB so delivery latency and bridge
  memory remain bounded. Switching between raw and grouped forwarding, or
  changing the group timeout, resets the gap-replay epoch so coalescing latency
  is not carried into the new mode.
- Raw bridge priority mode exists only while a bridge is active. In normal
  operation, SerialService retains its original awaited/lossless handoff to the
  parser. While bridging, raw bytes are offered to the bridge first and the
  parser handoff becomes non-blocking. The bridge offer itself never waits; a
  full chunk/byte budget faults that bridge session, while physical RX and the
  device connection continue. Parser/log overload is counted separately.
- Manual TX during a bridge uses a single pending slot. Existing bridge traffic
  has priority, both directions must be idle for the configured guard interval,
  and a payload that has started cannot be interleaved with bridge bytes.
- Manual TX state changes have a dedicated low-frequency event. Waiting,
  Sending, and terminal Idle transitions are dispatched to command UI
  immediately without forwarding high-frequency bridge counters to the UI.
- Virtual-to-device HEX display logs flush at 256 bytes, 25 ms idle, or 50 ms
  maximum latency, whichever applies first. Event, highlight, and view-filter
  rules all evaluate the same individual `LogLine.RawBytes`; patterns split
  across display records do not match. Raw transport queues and timing are
  independent of this processing.
- Virtual-to-device completion never waits for UI rendering. Its optional TX
  log input and output enter bounded queues before the existing bounded UI-only
  queue; saturation is counted and may omit file/event/UI records while raw
  transport continues unchanged.
- UI work is marshalled through the WinUI dispatcher and appended in batches.
- A `LogTextBatch` that reports retained-character trimming is never appended as
  a stale xterm delta after a covering snapshot. In the normal live path its new
  text is still appended and acknowledged exactly once, then a low-frequency
  replace-from-current-`LogViewModel` snapshot reconciles the evicted prefix. If
  a snapshot already covers the batch sequence, generation/boundary checks skip
  that delta while completing its host-side accounting. Retention-only snapshots
  have a 30-second monotonic cooldown measured from completion of the previous
  full snapshot, so even a render lasting longer than 30 seconds cannot trigger a
  back-to-back trailing replacement. One non-repeating dispatcher timer owns the
  sole delayed retention request; trims during an active snapshot or cooldown set
  only one pending bit and the eventual render reads the latest retained snapshot.
  If a filter-hidden record evicts an older visible prefix, `LogViewModel` emits
  a trim-only mutation (`AppendedText` empty and `LineCount` zero). The host does
  not issue an empty xterm append or acknowledgement for that mutation; it only
  enters the same bounded retention reconciliation policy. Thousands of such
  mutations therefore still use one pending bit and one dispatcher timer.
  Error recovery, clear, settings, navigation, and restore renders are not
  rate-limited and replace any delayed retention request. Each snapshot advances
  the xterm generation so deltas covered by it cannot be appended afterward.
  While minimized or rendering-paused, trim deltas may be discarded and collapsed
  into one full render on restore. During the cooldown xterm's own row-bounded
  scrollback can temporarily retain a different oldest prefix than the ViewModel,
  while current live RX continues to appear through normal delta appends.
  Completed full renders record retained line count, snapshot character count,
  actual 64-KiB transport chunk count, and duration in the view-model diagnostic
  state. These are workload measurements, not a WebView2 frame-time guarantee.
- Visible logs and events are bounded; complete history belongs on disk. The
  default log-view proxy budget is 256 MiB in addition to the existing default
  50,000-line ceiling (the configured line ceiling may vary). A single
  no-newline partial RX visual line is capped at 256 Ki
  characters. Reaching that cap creates a UI-only visual boundary; it does not
  add a parser or file-log packet boundary.
- Long-running workers accept cancellation and catch/report non-cancellation
  failures.
- Connect-attempt cancellation is separate from the established receive-session
  lifetime. Each serial generation owns its receive channel, read-stop token,
  force-abort token, worker, and port. Manual disconnect and shutdown first
  prevent another read and stop serial/bridge production. If the current read
  already returned bytes, its publish remains owned by that generation; the
  receive worker completes only its own byte channel after that publish.
  `LogPipeline` then naturally drains that
  completed input (including its terminal/HEX partial), completes its log
  channel, and the log observer drains it into event and file ingress. Event
  detection/output observers drain next; only then is file ingress deactivated
  and the file writer given its bounded drain/flush window. Automatic reconnect
  uses the same producer -> pipeline -> log-observer transport boundary, while
  retaining the logical session's event detector and file writer between
  attempts. Its attempt captures the current armed-session generation and may
  only commit if that generation remains armed. Automatic success never writes
  the armed flag; therefore a concurrent manual disconnect/disable is the final
  owner and a stale successful transport is retired before success is published.
- The established-session chain has a 30-second production drain ceiling and is
  further limited by a shorter caller/shutdown deadline (currently eight seconds
  at window close). If that deadline wins, the incomplete stages remain owned,
  forced cancellation is requested, a cleanup diagnostic states that accepted
  tail data may have been lost, and a new session is rejected until the old
  pipeline/observer has actually exited. This keeps shutdown finite without
  silently claiming a complete drain.
- A serial receive worker that ignores read cancellation past its bounded
  transport-stop deadline is force-aborted. Its channel is completed with the
  forced-stop error and the detached context remains tracked until the worker
  exits. A service instance retains at most four such unfinished receive
  contexts; reconnect is explicitly rejected at that limit. A late old worker
  can touch only its captured channel and port and cannot publish into or
  complete a newer generation. Graceful and forced stops emit distinct status
  and diagnostic reasons.
- Session-scoped receive faults re-check ownership while holding the same state
  gate used to install and detach receive sessions. An old worker that resumes
  after a replacement therefore remains diagnostic-only and cannot set the new
  connection to `Faulted` or replace its last error. Raw bridge chunks carry the
  source receive-generation ID; a bridge session binds that ID when it starts
  and rejects mismatched chunks under its queue/state gate. This prevents an old
  callback that passed cancellation immediately before a reconnect from entering
  the new bridge without invoking external subscribers under a lifecycle lock.
- A bridge generation likewise owns its virtual port, both bounded direction
  queues, cancellation, arbiter signal, and three workers. A timed-out old
  bridge context is isolated and tracked, with a four-context instance limit;
  new bridge starts are rejected until a slot is released. Late reader/writer
  completion cannot consume a newer queue, call its physical-device callback,
  or change its running state.

Serial and bridge native port lifecycle entry points are also isolated from the
caller thread. Port construction plus `Open`, and later `Close` plus `Dispose`,
run on `TaskScheduler.Default`. Open has a five-second hard wait and cleanup/stop
has one two-second hard wait in production. Each service instance tracks at most
four unfinished native lifecycle operations; new opens use only three slots so
one slot remains for the active port's final cleanup. A timed-out or canceled
open retains ownership until its late result is closed and disposed exactly
once. While three abandoned opens remain stuck, another connection/bridge start
is rejected explicitly; it becomes available again as slots complete. The app
owns one serial service and one bridge service for its window lifetime, so these
are bounded independently (four operations each). Native calls cannot be killed,
and a permanently stuck call keeps its slot and one scheduler thread until the
driver returns.

Manual disconnect and shutdown signal pending serial connect and bridge start
generation tokens and synchronously request that the established session start
no further native read before waiting for the MainViewModel lifecycle gate.
They do not prematurely complete its input channel: that ownership remains with
the captured receive worker so an already-returned chunk can publish first. A
late open from an older generation cannot publish `Connected`/`Running` or
install its port; its operation owner performs bounded late cleanup instead.

## Persistence

- Default profile: `%LOCALAPPDATA%\SerialMonitor\profiles\default.json`
- Serial logs: `%LOCALAPPDATA%\SerialMonitor\logs`
- Runtime diagnostics: `%LOCALAPPDATA%\SerialMonitor\diagnostics`

General startup/error/shutdown diagnostics use a dedicated single-consumer queue
with a total capacity of 128 operations, including the operation currently in a
blocked sink. Startup, error, and clear hot-path calls only perform a non-blocking
enqueue and never expose directory/file/sink failures. When full, newest ordinary
operations are dropped; a later accepted operation records the bounded drop summary in a
64 KiB rollover file. Individual diagnostic text is capped at 64 Ki characters.
This pump and its lock are separate from the 256-item file-writer incident pump,
so either diagnostic disk path may stall without blocking the other caller path.
Shutdown alone uses one additional staged critical slot per writer session, so a
full ordinary queue cannot discard the shutdown record before pre-close flush.
Before flush begins the latest shutdown value replaces an older staged value;
once its attempt is in flight or accepted into the FIFO, another shutdown value
is rejected to prevent duplicate persistence after a caller timeout. The window's
existing two-second pre-close call spends one monotonic absolute deadline waiting
for queue capacity, enqueuing the staged record behind all earlier work, and then
waiting for that record's sink operation to finish. It does not add a second
serial two-second barrier. Timeout before enqueue restores only the same single
staged slot; timeout after enqueue leaves ownership with the existing 128-item
FIFO and never re-enqueues it. Thus the maximum is 128 in-flight/queued work items
plus one not-yet-enqueued staged shutdown record. Sink exceptions and completion
races return failure without escaping to the caller or leaking a slot. A normal
successful pre-close flush makes the shutdown record durable and leaves the
session open so `OnClosed` can still record disposal errors; `OnClosed` then
completes and drains with its existing two-second bound. A permanently blocked
sink can still exceed the deadline and lose the final record during process exit.
A later app session will not create a second competing pump until the old pump
actually finishes.

Potentially process-ending failures (`OnLaunched` rethrow, XAML unhandled
exception, and AppDomain unhandled exception) are also offered to a separate
`fatal_runtime_error.txt` emergency path. It is isolated on
`TaskScheduler.Default`, waits at most 250 ms on the fatal caller, formats at most
64 KiB, and permits exactly one unfinished emergency sink operation process-wide.
If that filesystem call remains blocked, later fatal reports are rejected rather
than creating more tasks or handles; the slot becomes reusable after the late
operation completes. The normal bounded diagnostic enqueue is still attempted,
so this emergency file is a best-effort crash aid rather than a durability
guarantee.

Profile writes use a temporary file and replacement/backup flow. Generated
publish output under `release/` and `artifacts/` should be treated as build
artifacts rather than source.

## Verification

The minimum verification for every change is:

```powershell
dotnet build SerialMonitor.WinUI\SerialMonitor.WinUI.sln -c Debug
dotnet build SerialMonitor.WinUI\SerialMonitor.WinUI.sln -c Release
```

Use `MOCK` plus `docs/manual_test_checklist.md` for runtime regression checks.
Pure parsing, matching, buffering, and profile-normalization logic should gain
automated tests as it is extracted from UI-dependent classes.

The opt-in host-side maximum-line snapshot workload can be reproduced with:

```powershell
$env:SERIALMONITOR_RUN_MAX_CAP_SNAPSHOT_STRESS='1'
dotnet test SerialMonitor.WinUI.Tests\SerialMonitor.WinUI.Tests.csproj -c Release --filter "FullyQualifiedName=SerialMonitor.WinUI.Tests.XtermFullRenderTransportTests.MaximumLinePolicySnapshot_MaterializesAndSplitsExactly_WhenOptedIn" --logger "console;verbosity=detailed"
```

It materializes 500,000 synthetic rendered lines, verifies exact 64-KiB transport
chunk reconstruction, and reports characters, chunks, elapsed materialization,
and managed allocation observations. It deliberately does not claim to measure
WebView2 JavaScript execution, xterm layout, GPU composition, or interactive
frame latency; those remain manual soak items.

See `docs/code_review.md` for the current maintainability findings and staged
improvement plan.
