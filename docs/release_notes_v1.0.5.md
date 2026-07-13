# Serial Monitor v1.0.5

Released: 2026-07-13

## Fixes

- Fixed long HEX frames being split at artificial Windows native-read
  boundaries even though serial bytes were still arriving.
- Changed Windows native RX timing from RJCP's 100 ms total-read fallback to a
  10 ms inter-byte boundary with a 5,000 ms safety fallback. At 9600 bps this
  keeps variable frames up to 300 bytes intact while still ending a frame after
  an idle gap.
- Preserved the actual monotonic receive timestamp with every queued RX chunk,
  so HEX grouping uses arrival timing rather than delayed pipeline processing
  time.
- Changed the default HEX idle-grouping timeout to 10 ms.
- Fixed event popup messages in HEX mode to show hexadecimal byte previews
  instead of decoded replacement characters.

## Validation

- Verified a continuous 300-byte frame at simulated 9600-bps timing remains one
  HEX group.
- Verified a 300-byte frame with repeated sub-10-ms pauses remains one group.
- Verified small variable-length frames separated by more than 10 ms become
  separate groups.
- Verified exact preservation of binary bytes including `00`, `0A`, `0D`,
  `7F`, `80`, and `FF`.
- Verified fragmented Terminal-mode CRLF input is still reconstructed as one
  line.
- Completed Debug and Release x64 builds with zero warnings and zero errors.

## Notes

- Existing profiles keep their saved HEX timeout. Set `HEX timeout` to `10 ms`
  and reconnect the COM port to use the new native receive timing.
- The installer is unsigned, so Windows SmartScreen may show an
  unknown-publisher warning.
