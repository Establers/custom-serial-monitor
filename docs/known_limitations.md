# Known Limitations

This file tracks current intentional limits and validation gaps.

## Search

- Search is limited to the current visible/retained in-memory log buffer.
- Search does not scan full serial log files yet.
- Search Results uses manual refresh by default to avoid flicker and unstable
  selection while logs are appending.
- Each search is a snapshot of the retained buffer. Newly appended lines appear
  after Enter or Refresh, and very old results can become stale after trimming.
- Search Results groups repeated occurrences into one row per matching log line
  and materializes at most 1,000 lines per page. Prev/Next pages through the
  full snapshot while F3/Shift+F3 still navigates every occurrence.

## Settings Apply Behavior

- Some settings apply immediately.
- Serial settings that are unsafe during an active connection require reconnect.
- Terminal/HEX mode and HEX timeout changes apply inside the receive pipeline
  without disconnecting the active COM port.
- Some UI/log settings apply on the next app start or next new log file.
- Apply hints in Settings should be treated as the source of truth for when a
  setting takes effect.

## Hardware Validation

- MOCK and mock stress mode exercise the app pipeline, but they do not replace
  real hardware validation.
- Real COM long-running 72-hour validation is still pending.
- Real-device disconnect/reconnect behavior still needs coverage across common
  USB-UART adapters and Windows driver states.

## HEX RX Bus Utilization

- The top `RX BUSY / IDLE` meter is a rolling byte-count-based estimate and is
  visible only in HEX mode.
- `PEAK` is the highest fixed one-second bucket inside the same rolling
  60-second window, not an all-time maximum. It is unavailable during the first
  minute after a measurement reset.
- RS-485 does not standardize a utilization averaging window; 60 seconds is the
  application's monitoring convention.
- It uses the applied baud, data bits, parity, and stop bits. It does not inspect
  or decode the proprietary packet protocol.
- Local TX is intentionally not added, although adapter echo can reappear as RX.
  `IDLE` therefore means no successfully observed RX character time rather than
  proven electrical bus idle.
- Collisions, framing/parity errors, overruns, and bytes lost below the Windows
  API can make the estimate lower than actual physical wire activity.
- Clearing the visible log in HEX mode intentionally restarts the measurement
  window.

## COM Bridge

- The first bridge implementation supports one app-side virtual COM port and
  one external application on the opposite side of its virtual pair.
- Bridged data bytes are unchanged, but modem-control line forwarding
  (DTR/RTS/CTS/DSR), BREAK propagation, and multiple virtual outputs are not
  implemented yet.
- During an active bridge, raw transport is prioritized over parsing. Under
  extreme overload, parser/file/event/UI records may be incomplete and the
  bridge-priority parser-drop counters must be checked. With Bridge OFF, the
  original awaited RX pipeline is used unchanged.
- Virtual-to-device Terminal bridge logs group adjacent read chunks until a
  short idle boundary so split multibyte characters and keywords remain intact.
  Bridge log queue/drop/decode counters are reported separately; these logging
  conditions never change the forwarded bytes.
- In HEX mode, device-to-virtual chunks inside the active HEX idle timeout are
  coalesced and normally issued as one virtual-COM write matching the xterm
  packet line boundary. The writer waits for the smaller of the active timeout
  and its 100 ms maximum latency. Terminal mode retains immediate raw-chunk
  forwarding. Continuous HEX traffic is emitted at least every 100 ms and is
  also split at 1 MiB, preserving both bounded delivery latency and bounded
  memory.
- A single virtual-COM write still cannot require another process to receive the
  data in one `Read` call; Windows and the virtual-port driver expose a byte
  stream. Coalescing substantially reduces incidental splits but protocol-level
  framing remains the only absolute packet-boundary guarantee.
- Queue overflow intentionally faults the bridge instead of continuing with a
  silently incomplete byte stream. It does not disconnect the physical device.
- HEX bridge display records are capped at 256 bytes and 50 ms maximum latency.
  Event, highlight, and view-filter matching is record-local, so a HEX pattern
  split across two display records does not match. Physical byte forwarding
  remains immediate and unchanged.
- com0com or another virtual-port driver must be installed and configured
  separately. The application does not install or manage kernel drivers.

## Logging

- Serial logs are plain text; raw bytes received in HEX mode are written as
  byte-exact hexadecimal text.
- Raw binary `.bin` logging is not offered.
- Event detection and bounded context capture do not create a separate event log
  file.
- Optional log file names apply on the next LOG ON; historical files are never
  renamed.

## Command Workflows

- Command sequences send fixed ordered TX commands with delays.
- Expected-response automation is intentionally not implemented yet.
- Command parameter templates are intentionally not implemented yet.
- Sequence steps do not currently parse device responses or branch on output.

## UI Scope

- The UI is optimized for dense engineering use.
- It is not designed as a touch-first interface.
- Compact controls depend heavily on tooltips and the Help tab for
  discoverability.
- Very small windows can reduce comfort even when controls remain usable.

## Event Processing

- Event detection is keyword/rule based.
- Rule `Mode` is exclusive: an enabled Terminal rule is inactive in HEX mode,
  and an enabled HEX rule is inactive in Terminal mode. Terminal rules match
  decoded text; HEX rules match raw bytes.
- Event context capture is line based.
- MARK/session lines can appear in context, but they are not RX data and should
  not create events.

## Profiles

- Profile loading normalizes invalid or missing fields to safe defaults.
- Profiles using the former `MatchMode: Text/Hex` fields are migrated when read;
  newly saved profiles use `Mode: Terminal/Hex`.
- Older profiles should load gracefully, but newly added settings may use
  defaults until saved again.
