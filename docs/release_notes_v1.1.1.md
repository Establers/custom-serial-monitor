# Serial Monitor v1.1.1

Released: 2026-07-14

## HEX RX framing and diagnostics

- Treats short native reads associated with driver-reported Frame, Parity, or
  Overrun signals as untrusted instead of immediately declaring an idle packet
  boundary.
- Falls back to the exact profile HEX timeout for an error-affected read rather
  than forcing an additional split.
- Labels line-status counters as Windows driver reports and records nearby RX
  byte/chunk positions for analyzer correlation.
- Counts how often a native idle boundary was suppressed because of a
  line-status signal.

## HEX rules and logging

- Separates decoded payload text from byte-exact HEX presentation so event and
  context files no longer contain replacement characters for binary payloads.
- Keeps HEX event/context/file output in spaced uppercase byte form such as
  `45 52 52 4F 52`.
- Enforces mode-exclusive rules: while the app is in HEX mode only HEX event,
  highlight, and filter rules are active; Terminal mode activates only Terminal rules.
- Applies the same mode policy to both direct and precompiled rule paths.

## Raw COM bridge

- Physical RX now offers timing-aware chunks to the bridge without awaiting.
  Chunk or byte-budget overflow faults only the bridge and reports the slow
  consumer instead of silently continuing with missing bytes.
- Replays device-to-virtual receive gaps best-effort, including a minimum native
  idle boundary, and exposes delay/lateness diagnostics.
- Adds a bridge-owned physical TX scheduler with bridge priority, a 25 ms global
  idle guard, and exactly one pending manual TX slot. All manual entry paths use
  the same Busy state; command sequences are disabled while bridging.
- Adds queue byte counts, oldest age, overflow reason, last activity, and manual
  arbitration diagnostics.

## Validation

- Passed 35 Core tests and 54 WinUI tests (89 total).
- Passed COM4/COM5 com0com integration for bidirectional SHA-256 byte equality,
  manual TX arbitration/cancellation, and immediate bridge-only overflow fault.
- Passed repeated HEX/Terminal rule-exclusivity and bridge lifecycle/timing tests.
- Release build completed with zero warnings and zero errors.
- `git diff --check` completed successfully.

## Hardware validation status

- This build is published as a prerelease pending analyzer comparison on the
  affected USB-UART adapter and driver.
- The line-error-related false-split path is fixed. Packet groups separated by
  hundreds of milliseconds should remain distinct when the Windows driver
  preserves the idle boundary.
- Software cannot reconstruct an idle gap or a byte that an adapter/driver did
  not deliver. If occasional joins or splits remain with no Frame, Parity, or
  Overrun report, capture Diagnostics together with the analyzer trace, adapter
  model, driver version, baud/framing settings, profile timeout, and the exact
  affected byte range.
- The installer is unsigned, so Windows SmartScreen may show an
  unknown-publisher warning.
