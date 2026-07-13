# Serial Monitor v1.0.6

Released: 2026-07-13

## Fixes

- Removed the fixed 10 ms Windows native inter-byte boundary that could merge
  several short packets before the configured HEX timeout saw them.
- Changed RJCP/Win32 serial reads to immediate driver-buffer draining
  (`ReadIntervalTimeout = MAXDWORD`, total read timeout `0/0`). Native RX now
  transports available bytes without deciding packet boundaries.
- Removed the unrelated 500 ms stream-buffer idle wake-up. Disconnect
  cancellation still ends the receiver immediately.
- Kept HEX packet grouping exclusively in the application pipeline using the
  configured idle timeout and monotonic chunk-arrival timestamps.
- Added the last observed RX chunk gap to diagnostics for device-side timing
  verification.
- Added guidance below HEX timeout: driver latency must be shorter than the
  HEX timeout, which must be shorter than the real packet gap. Driver changes
  require disconnecting and reconnecting the COM port.

## Recommended timing

- For packets separated by at least 20 ms: driver latency `2 ms`, HEX timeout
  `10 ms`.
- For packets separated by about 3 ms: driver latency `1 ms`, HEX timeout
  `2 ms`.
- Very short timing-only boundaries are best-effort on Windows. If the USB
  driver combines multiple packets before exposing them, no application can
  recover missing boundaries without a protocol delimiter or length field.

## Validation

- Verified RJCP receives the exact immediate-drain setting: interval `-1`
  (`MAXDWORD`) and total timeout `0/0`.
- Verified eight fragmented 25-byte packets with observed 3 ms gaps become
  eight HEX groups at a 2 ms timeout.
- Verified observed 1.5 ms gaps merge at a 2 ms timeout.
- Verified a fragmented 200-byte frame with 1 ms chunk gaps remains one group.
- Verified already driver-merged bytes remain one group, documenting the
  information limit explicitly.
- Completed Debug and Release x64 builds with zero warnings and zero errors.

## Notes

- Reconnect the COM port after changing driver advanced settings.
- The installer is unsigned, so Windows SmartScreen may show an
  unknown-publisher warning.
