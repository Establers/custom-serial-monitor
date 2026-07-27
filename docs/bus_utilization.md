# HEX RX Bus Utilization

The compact meter to the right of the `HEX <timeout> ms` indicator estimates how
much of the recent receive-side serial line time was occupied by successfully
observed RX characters.

## Display

The meter is visible only in HEX mode.

```text
RX BUSY 71.0% · IDLE 29.0% · PEAK 93.0% · 1m
```

- `RX BUSY` is the estimated receive-side occupied time.
- `IDLE` is the remainder of the observation window.
- `PEAK` is the highest fixed one-second RX busy percentage still contained in
  the same rolling window. It is not an all-time session maximum; an old peak
  rolls out after 60 seconds. PEAK remains `--` until a complete 60-second
  window has been collected.
- The final field is the current observation duration. It increases from `1s`
  during warm-up, is displayed as `12s/1m`, for example, and becomes `1m` once
  a full rolling minute is available.
- While disconnected or before the first one-second sample, percentages are
  shown as `--`.

Local TX is intentionally not added. An adapter that echoes transmitted bytes
can still make those bytes reappear as RX. Therefore `IDLE` means "not occupied
by successfully observed RX characters," not a hardware measurement proving
that the physical bus was electrically idle.

## Calculation

The number of wire bits used by one received character is calculated from the
serial settings that were actually applied when the connection opened:

```text
bits per character = 1 start bit
                   + configured data bits
                   + parity bit (0 for None, otherwise 1)
                   + configured stop bits (1, 1.5, or 2)
```

For example, `9600 bps, 8N1` uses 10 bits per character. Receiving 480 bytes in
one second is therefore estimated as:

```text
busy time    = 480 bytes × 10 bits ÷ 9600 bits/second = 0.5 seconds
RX BUSY      = 0.5 ÷ 1.0 × 100 = 50%
IDLE         = 100% - 50% = 50%
```

Every second the UI reads the serial service's cumulative RX byte counter. It
stores only enough counter samples for a bounded 60-second sliding window. It
does not add work to the serial receive callback and does not retain packet
payloads.

Once a complete minute is available, `PEAK` divides that rolling minute into 60
fixed one-second buckets. Cumulative byte counts at exact bucket boundaries are
interpolated between neighboring timer samples. This prevents a short first
sample immediately after Connect, HEX entry, or Clear from being treated as a
full one-second peak.

During the first minute, the denominator is the actual elapsed observation time
so the result does not incorrectly treat the time before connection as idle.
After 60 seconds, the oldest samples roll out and the value always represents
the most recent minute.

## Start And Reset Cases

Measurement starts with an empty baseline in either of these cases:

- a serial connection succeeds while HEX mode is selected;
- the user changes from Terminal to HEX while already connected.

The measurement stops and its previous values are discarded when the app
disconnects or leaves HEX mode. A later reconnect or return to HEX begins a new
warm-up window; hidden Terminal traffic is not included retroactively.

Clearing the visible log while in HEX mode also resets the meter. This provides
a simple manual `t1`: press Clear, run the test, then read the current values at
the desired `t2`. Clearing in Terminal mode has no bus-meter side effect.

BUSY and IDLE are available after the first approximately one-second sample; the
app does not wait for a full minute for those averages. Until 60 seconds have
elapsed, they describe only the displayed warm-up duration and PEAK remains
`--`.

If an observed rate exceeds the theoretical configured line rate, `RX BUSY` is
clamped to 100%. This can occur with the MOCK generator or unusual driver and
timestamp behavior.

## Measurement Limits

- Windows serial APIs do not expose a continuous electrical busy/idle signal.
  This is a byte-count-based estimate, not an oscilloscope measurement.
- RS-485 specifies the electrical physical layer but does not standardize a bus
  utilization averaging window. The app's 60-second average plus rolling
  approximately one-second peak is an operational convention chosen for stable
  long-running monitoring.
- Local TX bytes are not added by design. Actual physical-bus utilization is
  higher while this application transmits, unless the adapter echoes those
  bytes and they are observed again as RX.
- Bytes lost because of collisions, framing errors, parity errors, driver
  overruns, or hardware buffering cannot be reconstructed and can make the
  estimate lower than actual wire activity.
- USB-UART and Windows driver buffering can move a byte-count increase between
  adjacent one-second samples. The rolling one-minute result is less sensitive
  to that short-term batching.
- Handshake pauses do not count as busy time unless RX characters are actually
  observed.

Use the existing driver error counters together with the meter when diagnosing
a shared bus.

## References

- [Analog Devices AN-727](https://www.analog.com/en/resources/app-notes/an-727.html)
  explains that RS-485 defines the physical layer while user software or a
  higher-level protocol controls arbitration, and identifies the all-drivers
  high-impedance condition as bus idle.
- [Analog Devices AN-960](https://www.analog.com/en/resources/app-notes/an-960.html)
  describes the multipoint/half-duplex driver-enable behavior.
- [Microchip USART frame documentation](https://onlinedocs.microchip.com/oxy/GUID-A9964E93-D46C-42E6-98D2-4ED783ABB2CE-en-US-2/GUID-7BA3A2AA-EFBF-4C3A-BB96-17B8A413DE69.html)
  documents start, data, optional parity, stop, and idle portions of an
  asynchronous serial frame.
