# Serial Monitor v1.0.7

Released: 2026-07-13

## Fixes

- Fixed intermittent merging of short, variable-length HEX packets when
  several Win32 serial-read completions accumulated in RJCP's managed buffer
  before the application receive thread ran.
- Preserved each native `ReadFile` completion boundary before RJCP's public
  `Read()` and `BytesToRead` APIs can flatten multiple completions into one
  accumulated byte buffer.
- Removed the duplicate HEX timeout wait. A native read that already completed
  after the profile idle timeout now closes its HEX group without applying the
  same timeout again in the log pipeline.
- Kept the saved profile timeout exact. No timeout is selected or changed from
  the baud rate; the UI recommendation remains informational only.
- Kept variable-length packet support with no fixed header, delimiter, packet
  length, or byte pattern assumptions.
- Moved real serial RX onto a dedicated long-running receive thread and kept
  byte publication separated from UI rendering and file logging.
- Subscribed to serial error events before opening the COM port so immediate
  frame, parity, overrun, and RX-buffer warning signals are not missed.
- Clarified that RJCP 3.0.5 `RXOver` is an 80% driver-buffer warning, while
  `Overrun` indicates an actual driver overflow.

## Diagnostics

- Added serial RX, pipeline-processed, HEX-accepted, HEX-emitted, and pending
  byte counters for end-to-end conservation checks.
- Added serial Frame/Parity/Overrun/RX-buffer-warning counters and the latest
  line-status signal.
- Added the active native idle timeout, last RX chunk gap, last HEX group byte
  count, queue depth, and drop counters to diagnostics and health status.
- Added a baud/frame-time recommendation as a starting point without
  automatically changing the user's profile.

## Tests

- Added maintained Core and WinUI test projects with variable packet lengths
  from one byte through hundreds of kilobytes.
- Added regression coverage for several native completions accumulating before
  the consumer runs; 11-byte, 7-byte, and 129-byte packets remain three HEX
  groups instead of one merged group.
- Added coverage for exact timeout boundaries, native-timeout latency, RJCP
  circular-buffer wrap/full cases, large HEX streaming groups, and byte
  conservation.
- Passed 35 Core tests and 21 WinUI tests (56 total) in Release x64.
- Repeated the seven native-boundary regression tests 20 times without failure.
- `git diff --check` completed successfully.

## Recommended verification

- For 9600 8N1 with continuous bytes and a measured 4 ms idle between complete
  packets, 2 ms is a valid profile starting point because one character is
  approximately 1.042 ms.
- For 38400 8N1, one character is approximately 0.260 ms; use the saved profile
  value that matches the measured packet-to-packet idle.
- Reconnect the COM port after changing HEX timeout or Terminal/HEX mode so the
  native receive timing is reapplied.
- This build remains a prerelease until the target USB-UART adapter and driver
  pass the documented analyzer-based hardware acceptance test.

## Notes

- UART packet bytes are assumed to be transmitted continuously. All timing
  values in the hardware matrix refer to idle between complete packets, not
  intentional idle inserted inside a packet.
- The installer is unsigned, so Windows SmartScreen may show an
  unknown-publisher warning.
