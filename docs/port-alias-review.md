# Port alias input and storage review

Date: 2026-10-03

The top-right name button edits the selected port's optional window name.
The title uses `Main board · COM3 - Serial Monitor`; aliases do not change
serial settings, command history, profiles, log filenames or xterm contents.

## Reproduced issues and fixes

- A null draft threw `NullReferenceException`. The editor now normalizes null
  to an empty draft, which removes only that port's alias.
- Malformed or overlong port values restored from an edited profile threw from
  the UI selection callback. Invalid values now disable naming and use the
  plain application title without retaining another port's alias.
- Unpaired UTF-16 surrogates were silently replaced by JSON serialization.
  Validation now rejects them while permitting valid emoji pairs and joiners.
- Dictionary JSON deserialization silently overwrote duplicate keys. Explicit
  object-property parsing now rejects duplicate keys, including case variants,
  and preserves the original file on failure.

Control characters, line separators and bidirectional control marks are also
rejected, so input cannot hide or reorder the COM identity in the title.
Ordinary punctuation, quotes, XML/script-like text and path-like names remain
literal strings. Alias input is not used as a file path or executable command.

## Storage and lifecycle checks

- Names have a maximum of 64 UTF-16 code units, matching the textbox limit.
- JSON storage is bounded to 256 KiB and 512 ports. Invalid roots, value types,
  Unicode, excessive entries, oversized files and malformed JSON are rejected.
- Reads deny concurrent in-place writes while allowing atomic file replacement;
  the validated input size cannot grow during deserialization.
- Writes acquire an OS file lock, read the latest file, merge one changed port,
  and replace it via a unique temporary file. Two-process saves preserve both
  ports. Cancellation and lock timeout preserve previously saved data.
- Applying captures the port and validated name before awaiting storage. A port
  change or popup dismissal invalidates the editor without redirecting a pending
  write to a different port. Rapid repeat submission cannot start a second save.
- Closing cancels pending work. Late load failures do not notify disposed UI;
  late initialization and popup-focus callbacks skip closing windows.

## Verification scope

PortAliasServiceTests and PortAliasViewModelTests cover input boundaries,
literal Unicode/punctuation round trips, 2,000 deterministic random UTF-16
inputs, corrupted storage, two real writer processes, timeout/cancellation,
port changes during saves and disposal during pending work.

Debug and Release solution builds passed with zero warnings and errors. The
full Debug automated suite passed all 628 tests (35 Core and 593 WinUI), including
45 port-alias cases and the 2,000-input check.

Native GUI interaction was interrupted during the preceding change; popup
focus, IME behavior and narrow-window layout still need the manual checks in
`manual_test_checklist.md`. Hardware serial soak coverage is unchanged.
