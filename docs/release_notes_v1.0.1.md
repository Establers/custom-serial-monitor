# Serial Monitor v1.0.1

Released: 2026-07-13

## Improvements

- Added lossless HEX RX grouping based on the idle time after the last received
  bytes.
- Added a configurable `HEX timeout` setting from 1 to 5,000 ms with a 40 ms
  default.
- Preserved every raw HEX byte, including `00`, `0A`, `0D`, and `FF`, without
  treating CR/LF as line endings.
- Removed the previous 128-byte HEX display split. Large continuous inputs use
  bounded internal segments while remaining one visual line.
- Applied HEX timeout changes immediately to a currently open group.
- Reduced UI pauses after the retained log buffer reaches its configured line
  limit by avoiding full-buffer-sized allocations for every incoming batch.
- Added concise English tooltips to inspector tabs and queue/drop explanations
  to the Health footer.

## Validation

- Verified exact byte preservation with a 300-byte multi-chunk payload containing
  `00`, `0A`, `0D`, and `FF`.
- Verified idle gaps create separate HEX groups.
- Verified an 80 KB continuous input remains one visible HEX line.
- Verified live timeout shortening and extension on an open HEX group.
- Release build completed with zero warnings and zero errors.

## Notes

- The default HEX grouping timeout is 40 ms. Protocols with strict inter-frame
  timing, such as Modbus RTU, may need a shorter value.
- The installer is unsigned, so Windows SmartScreen may show an unknown-publisher
  warning.
- com0com or another virtual COM driver must be installed separately when using
  the COM Bridge feature.
