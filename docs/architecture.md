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

## Local AI / CLI automation

The GUI hosts a current-user-only Named Pipe endpoint per runtime instance.
`serialctl` provides JSON commands and an MCP stdio server; neither opens a COM
port. `AutomationController` coordinates control reservations, bounded capture
windows and paging. A dispatcher adapter uses the existing GUI connection/TX
paths, including TX logging. GUI TX, sequences and bridge startup cannot enter
an active automation reservation; an active GUI writer/sequence/bridge/reconnect
causes an external mutation to return busy. Explicit GUI disconnect remains an
escape hatch and interrupts a capture.

`AutomationControlGate` reserves bridge startup before its first asynchronous
wait, so a bridge whose port is still opening also excludes automation. During
an automation reservation, GUI connection/settings controls are disabled and the
GUI connect entry point rechecks ownership after acquiring its lifecycle gate.
`AutomationConnectionOperation` cancels the shared port-refresh wait, forwards the
request token to connection startup, and verifies the actual port/baud afterward.
A startup-only cancellation registration cancels the independent connection
lifetime during startup and detaches before the request ends, preserving an
established session when the RPC host closes that request.

`SerialService.CaptureBytesReceived` publishes the receive generation and original
chunk in every receive mode, before parser/bridge routing. Its only subscriber
offers the chunk to `RxCaptureService` without waiting on disk or UI. This is a
separate fan-out, not another reader of the serial/parser channels. Capture
workers persist byte-exact RX and incrementally decoded text, independent of
display mode, pause and filtering. Cancellation/session changes stop collection;
bounded queues, byte limits, disk failures and serial line errors remain explicit
in results. Large RX is paged with byte offsets and UTF-8 boundaries, rather
than silently truncated. Matching is not required, and capture completion does
not imply product command success. See [AI control](ai_control.md).

Capture retention obtains non-reading, delete-sharing leases on every member of
a capture before deleting any member, with metadata removed last. Active page
read handles and external viewers cause the whole capture to be skipped; cleanup
tries another old capture and never fails a new capture for a sharing violation.
Opening a page and checking its metadata are synchronized with retention. If all
old captures are pinned, retention limits are temporarily exceeded until a later
capture can clean them up.

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
  Terminal decoder state survives idle and 64 KiB display boundaries, and is
  finalized at stream end or an encoding/mode change. Bridge stop joins the
  producer, flushes accepted log input, and waits for file/event fan-out before
  those consumers stop. Start/stop operations are serialized through this drain.
- `LogViewModel` owns the bounded visible snapshot and xterm-specific formatting.
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
- Bridge HEX previews remain limited to 64 bytes on screen. Live files and
  history exports use the full HEX text for each record; file queue memory
  accounting includes this separate text.
- Virtual-to-device completion never waits for UI rendering. Its optional TX
  log input and output enter bounded queues before the existing bounded UI-only
  queue; saturation is counted and may omit file/event/UI records while raw
  transport continues unchanged.
- UI work is marshalled through the WinUI dispatcher and appended in batches.
- Visible logs and events are bounded; complete history belongs on disk.
- Long-running workers accept cancellation and catch/report non-cancellation
  failures.
- Shutdown stops producers before consumers and gives writers a bounded interval
  to drain and flush.

## Persistence

- Default profile: `%LOCALAPPDATA%\SerialMonitor\profiles\default.json`
- Window names by port: `%LOCALAPPDATA%\SerialMonitor\port-aliases.json`
- Serial logs: `%LOCALAPPDATA%\SerialMonitor\logs`
- Runtime diagnostics: `%LOCALAPPDATA%\SerialMonitor\diagnostics\instance-<pid>-<launch-id>`
- WebView2 runtime data: `%LOCALAPPDATA%\SerialMonitor\WebView2\instance-<pid>-<launch-id>`

Profile writes use a temporary file and replacement/backup flow. Generated
publish output under `release/` and `artifacts/` should be treated as build
artifacts rather than source.

Port aliases are independent of profiles and serial-log filenames. Each edit
asynchronously merges only the selected port's name into the latest alias file
under an OS file lock, then atomically replaces it. Renaming needs no reconnect.
The button stays outside the connection toolbar's scrolling content, and the
native window title includes the selected port and its optional alias.

## Multiple app instances

Each launch owns its serial/bridge services, parsers, bounded channels, event
detector, command sequence runner, file writer, UI log buffer and xterm page.
There is no runtime IPC or watcher that copies those states between launches.
Profile and update preference files remain shared intentionally.

WebView2 uses a distinct user data folder with exclusive access for every launch,
so its browser process, profile preferences and browser storage are isolated.
The terminal receives the resolved theme from its own window. Runtime diagnostics
also use distinct paths so another launch cannot clear an error or change the
health indicator. Browser caches are removed after their browser exits; subsequent
startups reclaim abandoned caches using an exclusive owner lease and never touch
another active launch's folder.

OS resources remain shared: two launches cannot open the same COM port, and the
clipboard is global. Automatic serial log names are allocated with CreateNew and
collision suffixes; explicitly choosing an existing filename is rejected without
overwriting the other capture. See [instance validation](instance-isolation-validation.md).

## Verification

The minimum verification for every change is:

```powershell
dotnet build SerialMonitor.WinUI\SerialMonitor.WinUI.sln -c Debug
dotnet build SerialMonitor.WinUI\SerialMonitor.WinUI.sln -c Release
```

Use `MOCK` plus `docs/manual_test_checklist.md` for runtime regression checks.
Pure parsing, matching, buffering, and profile-normalization logic should gain
automated tests as it is extracted from UI-dependent classes.

See `docs/code_review.md` for the current maintainability findings and staged
improvement plan.
