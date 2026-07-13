# Serial Monitor v1.1.0

Released: 2026-07-13

## UI and usability

- Kept the inspector below the main log at every window width and added a
  compact collapse/expand control that does not reserve vertical space while
  the inspector is hidden.
- Added compact left/right navigation controls to overflowing connection, log,
  TX, and Quick command toolbars while keeping their native scrollbars hidden.
- Kept the health footer scrollbar unchanged for direct access to all runtime
  counters.
- Replaced text-heavy search, rendering, and clear controls with compact
  previous/next, pause/play, and clear icons.
- Removed outlines from circular navigation buttons and added distinct normal,
  hover, pressed, and disabled background states for smoother DPI rendering.
- Unified Connect and Disconnect into one fixed-width button and aligned the
  Log ON/OFF button height with it.
- Aligned connection status to the left and made the Bridge COM selector
  consistent with the main COM-port selector.
- Removed the unused Custom Marker button, rendering status sentence, and TX
  Mode selector.

## Fixes

- Replaced the hidden-tab Context WebView with a native text viewer bound
  directly to the selected event context.
- Fixed the blank Context body that could appear after selecting an event and
  switching from Events to Context.
- Preserved multiline context formatting, text selection, copy support, and
  horizontal and vertical scrolling.
- Prevented TX and other toolbar controls from disappearing after repeated
  narrow/wide window resizing.

## Stability

- The serial RX, parsing, event detection, asynchronous file logging, bounded
  UI log buffer, and batched rendering pipelines are unchanged.
- Passed 35 Core tests and 21 WinUI tests (56 total).
- Release build completed with zero warnings and zero errors.
- `git diff --check` completed successfully.

## Notes

- This is the stable v1.1.0 release, not a prerelease.
- The installer is unsigned, so Windows SmartScreen may show an
  unknown-publisher warning.
- WebView2 Runtime remains required for the xterm.js main log view.
