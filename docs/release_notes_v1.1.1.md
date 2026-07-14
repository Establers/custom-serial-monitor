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
- Enforces mode-exclusive rules: HEX RX/TX/bridge lines accept only HEX event,
  highlight, and filter rules; Terminal lines accept only Text rules.
- Applies the same mode policy to both direct and precompiled rule paths.

## Raw COM bridge

- Replaces full-queue `TryWrite` drops with bounded asynchronous backpressure
  in both bridge directions.
- Cancels a blocked bridge enqueue with the bridge lifetime and treats an
  intentional stop as normal completion rather than `Raw RX observer failed`.
- Derives pending counts from the channel itself, eliminating the shutdown
  reset/decrement race that could show a negative pending value.
- Does not count cancellation caused by intentional bridge shutdown as a
  transport error or data drop.

## Validation

- Passed 35 Core tests and 34 WinUI tests (69 total).
- Passed repeated HEX/Text rule-exclusivity and bridge lifecycle/backpressure
  test runs.
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
