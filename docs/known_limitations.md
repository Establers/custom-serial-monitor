# Known Limitations

This file tracks current intentional limits and validation gaps.

## Search

- Search is limited to the current visible/retained in-memory log buffer.
- Search does not scan full serial log files yet.
- Search Results uses manual refresh by default to avoid flicker and unstable
  selection while logs are appending.
- Search result jumps are based on visible-buffer order and xterm search
  behavior; very old results can become stale after visible-buffer trimming.

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
- Device-to-virtual idle gaps are replayed best-effort from monotonic receive
  timestamps. Windows scheduling and virtual COM buffering do not guarantee
  sub-millisecond timing; Diagnostics reports delivery delay and lateness.
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
