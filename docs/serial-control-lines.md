# Serial control lines

Settings → Serial places DTR/RTS output controls beside the existing Lines setting.
CTS, DSR, DCD and RI input states appear immediately underneath. DCD is Windows
RLSD; RI is RING. An additional RTS TX Toggle option selects driver-controlled
RTS during transmission, as in Tera Term's RTS Toggle mode.

## Display and controls

- Connected inputs show driver-reported logical ON/OFF. A background observer
  samples them every 250 ms; the existing one-second UI status batch presents
  the latest snapshot. This display is not an edge recorder and can miss pulses.
- A status query stalled for over two seconds invalidates the old snapshot;
  the UI shows N/A rather than continuing to display stale ON/OFF values.
- Disconnected inputs show `—`. Unsupported or failed queries show `N/A` with
  the reason in the hint/tooltip. MOCK never invents physical input readings.
- Output ON/OFF, AUTO (hardware handshake), TX (RTS transmission toggle), and
  N/A describe the driver configuration, not measured output voltages.
- Manual DTR/RTS changes use the currently open Windows handle. Successful
  driver readback updates the displayed state, current profile settings and
  reconnect settings, and adds one SYS record to the normal asynchronous log.
- Hardware handshaking locks only the output it owns. RTS TX Toggle also locks
  manual RTS; disabling it restores the last manual RTS choice. Conflicting
  RTS handshaking/TX Toggle settings are rejected, or normalized when loading
  a profile. Existing profiles default to TX Toggle OFF.
- GUI changes are disabled during connection transitions, sequences and other
  AI/CLI control. Direct terminal input stays enabled during output changes:
  driver changes join the same bounded input queue, after earlier keystrokes and
  before later keystrokes, then serialize through the service write gate. Serial writes and connection operations
  serialize with output changes. Queued changes cannot cross connection
  generations. Cancellation before driver work leaves the outputs untouched.

## Live serial settings and automation

Baud, data bits, parity, stop bits and handshake can be changed in the GUI while
connected. GUI and `serial_configure` share the same driver transaction: drain
accepted TX using the previous framing, update the existing handle, verify the
driver's readback, and restore the previous DCB on failure. An unverified restore
faults the connection. Neither path reopens the port or purges buffered RX/TX.
Hardware/driver support determines which data/stop-bit combinations are accepted;
the app does not add restrictions beyond the advertised setting ranges.
Selecting RTS handshake disables RTS TX Toggle; other changes preserve the
last manual DTR/RTS choices. Direct input is not stopped by these transactions.

`serial_get_control_lines` / `serialctl lines` returns the current output modes,
input snapshot, availability/error, port and session generation without reserving
TX. Unavailable inputs are JSON null, not false. Input changes are not recorded
as events; polling and UI batching can miss pulses.

`serial_set_control_line` accepts `line: dtr|rts|rts_toggle` and boolean `enabled`.
An optional expected `port` and `sessionGeneration` reject a mismatched target
before mutation. Mutations reserve control exclusively and retain the original
connection generation. AUTO outputs cannot be overridden manually. Read status
after any failed/uncertain response instead of automatically repeating a change.

```powershell
serialctl lines --instance INSTANCE_ID
serialctl lines --dtr on --port COM7 --generation 123 --instance INSTANCE_ID
serialctl lines --rts-toggle on --port COM7 --instance INSTANCE_ID
serialctl configure --baud 57600 --data-bits 7 --parity even --stop-bits 2 --handshake none --instance INSTANCE_ID
```

The CLI changes one output per command so each result identifies the verified
change. The GUI/CLI do not provide inverted RTS polarity or RS-485 turnaround
delays. Actual RS-485 timings and electrical output levels require hardware
verification. Tooltips describe each input/output separately and explain
`—`, `N/A`, the approximately 250 ms sampling and one-second display interval.

RTS transmission timing is performed by the Windows serial driver; the app does
not sleep or change direction once per RX/TX callback. Adapter wiring, driver
support and actual transmitter-empty behavior determine suitability for an
RS-485 converter. Software cannot establish electrical direction correctness
from a successful DCB readback alone. A DTR/RTS transition can reset a board if
that is how the board is wired.

## Implementation and failure handling

SerialControlLineRuntime owns the single background observer and serialized
control IO for a connection. Input query failures retain unknown values and
retry; errors appear in the UI without claiming OFF. The serial receive and
logging pipelines are unchanged. A failed write attempts a verified restoration;
unverified restoration faults the connection instead of continuing TX.

An output operation has a two-second caller deadline. If a synchronous driver
IOCTL outlives it, that one operation remains owned by its original connection.
TX is faulted and handle retirement waits for the late operation and observer.
The existing bounded disconnect/pending-teardown path fences reopening.

WindowsSerialControlLines uses GetCommState, SetCommState, GetCommModemStatus
and EscapeCommFunction on RJCP's existing SafeFileHandle. RJCP 3.0.5 does not
publicly expose that handle or support RTS_CONTROL_TOGGLE; access to its pinned
private handle field is isolated and covered by a compatibility test. Recheck
this adapter when updating RJCP. Baud updates preserve the full driver DCB,
including TX Toggle; there is no port reopen or RX/TX purge.

## Verification

The control-line tests cover handshake ownership, N/A presentation, generation
fences, queued cancellation, failed writes/readback/restoration, input recovery
and profile compatibility. Opt-in tests use only the registry-verified
CNCA0/CNCB0 com0com emulator pair, checking peer CTS/DSR/DCD changes, native
handshake locks, DCB Toggle preservation after baud updates and binary RX/TX.
These checks validate software and virtual-driver behavior, not physical
RS-485 adapter timings.

## References

- [Tera Term serial controls](https://teratermproject.github.io/manual/5/en/menu/setup-additional-serialport.html)
- [Windows DCB modes and handshake restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-dcb)
- [GetCommModemStatus](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getcommmodemstatus)
- [EscapeCommFunction](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-escapecommfunction)
- [Pinned RJCP source](https://github.com/jcurl/RJCP.DLL.SerialPortStream/tree/6c1bd0e4f9f51b72fe88971157198ab1fa1e34b9)
