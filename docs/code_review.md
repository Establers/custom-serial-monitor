# Code Review and Improvement Plan

Review date: 2026-07-11

## Current assessment

The runtime pipeline follows the project's most important stability rules:
serial RX, parsing, file logging, event detection, and UI rendering are separate;
background handoffs are bounded; file writes are asynchronous; and visible UI
history is capped. A clean Debug build completes with no compiler warnings.

No automated test project is present, so parser, matcher, buffer, profile, and
lifecycle regressions currently depend on manual testing. This is the largest
verification gap.

## Review scope

No C# or XAML implementation changes are included in this review. The changes
are limited to documentation cleanup and recommendations. Architecture and
requirements references now point to canonical root documents.

## Improvement priorities

### P1: Add automated tests

Create a non-WinUI test project for logic that can run without a window:

- `LineParser` chunk boundaries, CR/LF/CRLF modes, and partial-line flushes.
- `EncodingDecoder` invalid bytes and supported encodings.
- `LogRuleMatcher` Terminal/HEX mode rules, direction, case, priority, and invalid input.
- `BoundedLogBuffer` overflow, resize, and large-batch behavior.
- `ProfileService` normalization, corrupt JSON recovery, and atomic-save fallback.
- `FileLogWriter` rotation/exact filename behavior with a temporary directory.
- `EventDetector` overlapping contexts, queue pressure, and cancellation.

### P1: Split the two oversized UI classes

`MainViewModel.cs` is about 10,000 lines and `MainWindow.xaml.cs` is about 3,800
lines. Their size makes lifecycle and threading changes hard to review safely.
Extract one responsibility at a time behind narrow interfaces:

1. Search state and result projection from `MainViewModel`.
2. Command history, shortcuts, and sequence execution.
3. Session/marker coordination.
4. Health snapshot construction and diagnostic text formatting.
5. xterm bridge, context WebView bridge, and editor-dialog factories from
   `MainWindow`.

Keep orchestration in `MainViewModel`; move reusable business rules and formatting
to testable services. Each extraction should preserve counters and pass the mock
stress checklist before the next one begins.

### P1: Surface command and dispatcher failures consistently

`AsyncRelayCommand` records unexpected exceptions to a diagnostics file but does
not guarantee an immediate user-visible error. Standardize an application error
sink that records the exception, updates health/status on the UI dispatcher, and
cannot throw back into a background worker or event handler.

### P2: Strengthen sustained-load verification

Automate a headless or service-level soak test using deterministic mock sequences.
Assert zero parser loss at supported rates, bounded pending counts, expected drop
counters under forced overload, clean cancellation, and final file flush. Keep
the interactive checklist for WebView2 and focus behavior.

### P2: Reduce synchronous diagnostic I/O

Runtime diagnostics are now failure-safe, but normal startup/shutdown/error paths
still use synchronous file I/O. A single bounded diagnostic writer would avoid
UI-thread stalls while retaining a synchronous last-chance fallback for fatal
process failures.

### P2: Add CI and release reproducibility

Add Debug/Release build jobs and unit tests on a Windows runner. Publish packages
from clean source, record dependency versions, and keep generated `release/` and
`artifacts/` content outside source control.

## Change discipline

Because this tool is used for long-running debugging, avoid broad rewrites of the
pipeline. Prefer small extractions with unchanged public behavior, explicit queue
capacity and cancellation semantics, build/test evidence, and a mock-stress run
after lifecycle or concurrency changes.
