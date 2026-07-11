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
- Some UI/log settings apply on the next app start or next new log file.
- Apply hints in Settings should be treated as the source of truth for when a
  setting takes effect.

## Hardware Validation

- MOCK and mock stress mode exercise the app pipeline, but they do not replace
  real hardware validation.
- Real COM long-running 72-hour validation is still pending.
- Real-device disconnect/reconnect behavior still needs coverage across common
  USB-UART adapters and Windows driver states.

## Logging

- Serial and event logs are plain text.
- Raw binary logging may be a skeleton setting if it is not fully implemented in
  the current build.
- Session-based filenames are intended for newly created or rotated files; avoid
  assuming all historical files will be renamed.

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
- Event context capture is line based.
- MARK/session lines can appear in context, but they are not RX data and should
  not create events.

## Profiles

- Profile loading normalizes invalid or missing fields to safe defaults.
- Older profiles should load gracefully, but newly added settings may use
  defaults until saved again.
