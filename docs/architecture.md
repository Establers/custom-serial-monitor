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
```

TX commands and user markers join the flow after parsing as `LogLine` objects.
Only RX data passes through the byte decoder. ANSI highlighting is generated for
the xterm view only; persisted logs remain plain text.

## Component responsibilities

- `SerialService` owns real and mock serial connections, RX byte reads, TX byte
  writes, and serial counters. It never touches UI controls or files.
- `LogPipeline` owns line framing, partial-line state, decoding, timestamps, and
  parser diagnostics. It publishes parsed lines through a bounded channel.
- `FileLogWriter` owns buffered asynchronous writes, rotation, session naming,
  flush, and file-write counters behind a bounded queue.
- `EventDetector` owns rule evaluation, before/after context capture, bounded
  pending captures, and asynchronous event-log persistence.
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
- UI work is marshalled through the WinUI dispatcher and appended in batches.
- Visible logs and events are bounded; complete history belongs on disk.
- Long-running workers accept cancellation and catch/report non-cancellation
  failures.
- Shutdown stops producers before consumers and gives writers a bounded interval
  to drain and flush.

## Persistence

- Default profile: `%LOCALAPPDATA%\SerialMonitor\profiles\default.json`
- Serial/event logs: `%LOCALAPPDATA%\SerialMonitor\logs`
- Runtime diagnostics: `%LOCALAPPDATA%\SerialMonitor\diagnostics`

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

See `docs/code_review.md` for the current maintainability findings and staged
improvement plan.
