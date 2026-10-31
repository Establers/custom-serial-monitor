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

The `serial-monitor` plugin packages the shared skill and a Windows stdio launcher.
Its marketplace is `serial-monitor-tools`, hosted in this repository and bundled
as an optional local source at `<app>\agent-plugin`. The launcher resolves the
installed CLI and performs a bounded read-only `describe --json` compatibility
check before starting MCP. It does not open COM, modify AI configuration or install
application updates. `serial_get_capabilities` exposes current CLI/server schemas
and control API metadata independently of a GUI. This metadata does not claim that
an already-running older GUI has the same features. See [plugin deployment](agent_plugins.md).

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
chunk in every receive mode, before parser/bridge routing. The automation subscriber
offers the chunk to `RxCaptureService` without waiting on disk or UI. GUI sequence
recording uses a separate bounded observer only while that GUI run owns execution. This is a
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

GUI sequence steps distinguish TX from RX Check. RX Check never invokes the
send delegate and waits its entire configured 0..600000ms, even after a match.
The pattern inherits the preceding enabled TX mode (or captured main mode), with
no separate response-format selector. Legacy Expect/CaptureMs retain their API
semantics; v2 attached conditions migrate into explicit RX steps. v3 RX patterns
are stored outside CommandText so older clients cannot transmit them.
`ResponsePattern` uses incremental literal KMP or wildcard Shift-And over raw
bytes, independent of receive chunk/packet boundaries. `SequenceRunSession`
serializes RX and control markers through a bounded channel. A pre-TX marker
anchors immediate replies; RX Check streams any intervening RX from disk in
bounded pages, then observes through its fixed deadline. History never carries
across repeats/connections. The worker writes segmented raw RX, fixed-width
receive-time/offset indexes and JSONL step/attempt records. Host timestamps derive
from monotonic receive timestamps anchored to wall time, independently of queue
latency. The UI reads bounded pages and incrementally decodes Terminal chunks.
V2 storage references the immutable definition instead of repeating commands and
patterns in every row, reconstructs presentation strings on read, and stores
relative tick timestamps without losing precision. RX index entries retain
explicit chunk lengths, timestamps and generations in 24 bytes. Disk capture
metadata also uses a sparse v2 format; CLI fields are reconstructed in full.
Readers continue to support v1 files. See [result storage](result_storage.md).
Results show original row numbers, repetitions, TX/RX times, match highlighting
and separate check/step states. Viewers pin runs against retention; orphaned
running manifests are interrupted. Queue/storage faults stop further GUI TX.
Closing the result window does not stop a run.

`SequenceRunObserver` joins the existing GUI runner to live checking and result
recording. GUI running state is acquired before async preparation and held through
result finalization, excluding CLI/MCP/bridge reservations and manual TX. Existing
GUI reconnect retry semantics remain explicit, with a distinct result per attempt.
The legacy automation API rejects active live conditions and overwrites of live
definitions (`unsupported_sequence_feature`) instead of silently losing conditions.
CLI/MCP retains its fixed-capture engine, limits, evidence and result API; GUI runs
do not take or cancel automation capture ownership.

## Component responsibilities

- `SerialService` owns real and mock serial connections, RX byte reads, TX byte
  writes, and serial counters. It never touches UI controls or files.
- `LogPipeline` owns line framing, partial-line state, decoding, timestamps, and
  parser diagnostics. It publishes parsed lines through a bounded channel.
- `FileLogWriter` owns buffered asynchronous writes, rotation, file naming,
  flush, and file-write counters behind a bounded queue.
- `EventDetector` owns rule evaluation, before/after context capture, bounded
  pending captures, and UI event notifications. Context rows are archived on its
  background worker; captures retain references instead of duplicate payloads.
  `EventEvidenceStore` preserves full context asynchronously. Event history and
  pending UI events each have a 16 MiB estimated-memory budget; context viewing
  reads bounded pages and full export streams every original row. The runtime
  archive is temporary; live serial logs and explicit exports remain permanent.
  Parser stream offsets expose dropped input and reset framing/decoder state so
  rules and sequence RX checks cannot join bytes across a missing range. Raw
  capture, bridge forwarding, and original live logs retain their own paths.
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
  Its screen projection has separate retained-data (64 MiB), rendered-text
  (16 Mi characters), and dispatcher pending-data (32 MiB) budgets alongside
  the record count. Continuous partial RX rows wrap at record boundaries without
  resetting streaming rule state. Ordinary native completions remain complete;
  only oversized individual records receive a defensive display preview.
  Eviction uses a low-water mark to avoid rebuilding on every incoming chunk.
  Full original records continue through event detection, sequence RX evidence,
  and the asynchronous live file writer independently of screen retention.
- The xterm navigation modules read the existing bounded OSC ID/timestamp ring.
  `log-navigation.js` resolves times/relative offsets/retained ordinals/percentages
  with binary searches for ordered timestamps.
  Clock regressions use bounded rolling block summaries. `log-navigation-terminal.js`
  tracks physical row anchors and trim offsets, rebuilding after column reflow in
  4 ms slices. Navigation refreshes never scan or decode the complete terminal buffer.
  The terminal uses its full available width and height without a time rail or
  activity graph. Hidden timestamp prefixes never affect navigation metadata.
  WinUI owns the on-demand ContentDialog opened by Ctrl+G or the fixed log toolbar's
  Go to button, using native controls and application theme resources; navigation
  has no inspector tab. `ILogNavigationBridge` transfers
  navigation metadata and results between the native view model and the terminal.
  WebView2 can encode integral timestamps with exponent notation, so the bridge
  validates their exact numeric value before converting to nullable Int64 fields.
  Raw transport/JSON errors stay in diagnostics. `LogTimePickerViewModel` captures
  the retained range and wall clock once when Ctrl+G opens, constrains native date/
  time selector choices to that frozen range. Idle-time selections use the last
  retained log's stable ID captured at opening, preserving the exact record even
  when timestamps are duplicated or new RX arrives. Expired IDs are rejected
  without a timestamp fallback. It has no polling or text-preview loop. The dialog uses
  brief status messages in fixed-height regions and cancels its work when closed.
  A successful action closes without a second metadata request. Opening sessions
  invalidated by minimizing or shutdown are discarded before showing a dialog.
  Metadata rings reserve viewport capacity in 256-row blocks, avoiding full ring
  copies during normal height changes. Width changes still invoke xterm's native
  history reflow, which remains a separate performance bottleneck at one million rows.
  A successful jump disables following locally before notifying WinUI;
  RX, file logging and view-pause state are unaffected.
  The Back stack contains at most 64 stable IDs with wrapped-row offsets.
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
- Terminal event, highlight, and view-filter matching carries only the necessary
  keyword suffix across consecutive partial RX records. A newline, RX sequence
  gap, content-mode boundary, or rule replacement ends that matching state.
  Filters restore only the unseen prefix needed to display a boundary match;
  retained raw records remain unchanged. HEX matching stays record-local and
  never joins byte patterns across records. Async filter rebuilds publish their
  matcher state with the formatted view so subsequent live RX uses the same rules.
- Visible logs and events are bounded; complete history belongs on disk.
- View filters are a projection of the entire retained log history. Changing the
  selection rebuilds that projection; ALL restores hidden retained records in
  their original order and with stable line IDs. Highlight rule snapshots remain
  tied to receipt time. Filtering never changes event detection or file logging.
- Filter formatting uses a detached snapshot on one cancellable worker. A bounded
  queue collects live records for catch-up before publishing on the UI thread.
  Clear, format/capacity changes, and catch-up overflow restart from current
  history so removed records cannot be restored by an older calculation.
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

## Keyboard shortcuts

`SerialMonitor.WinUI/Assets/xterm/app-shortcuts.json.js` is the single data-only
definition of built-in gestures, modifier variants and their action IDs. It is
embedded in the .NET assembly and loaded directly by the terminal and legacy
context page. Native handlers use `AppShortcuts.Matches`; browser handlers use
`serialMonitorShortcuts.matches`. Do not put key literals in handlers.

The same definitions generate reservation lookup tables once at startup. Adding
a gesture to an existing action therefore changes matching, capture validation,
profile validation and native/browser TX guards together. A new action also needs
an `AppShortcutAction` member and its handler; missing or duplicate definitions
fail catalog validation. Context-specific shortcuts remain reserved globally.
Existing profile bindings that conflict after an upgrade remain visible for
repair but cannot execute. Duplicate quick-command bindings are also inactive.

`build/ShortcutArchitecture.targets` runs before C# compilation in both build
configurations. `KEYMAP001` rejects raw native key literals, direct accelerators
and browser keyboard-property reads outside the two input adapters. Those adapters
translate input only; they must not implement application actions. The catalog
must remain a JSON assignment, without executable matching logic.

Regression tests enumerate every actual binding and modifier variant, the entire
recordable virtual-key space, and future registration of an extra gesture. Build
fixtures verify that bypasses fail compilation checks. CI also compares the
compiled native reservation set with the browser asset using
`scripts/test_shortcut_catalog.cjs`; this check needs no npm packages.

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
