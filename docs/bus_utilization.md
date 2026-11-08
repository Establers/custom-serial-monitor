# HEX RX Bus Utilization

The compact meter to the right of the `HEX <timeout> ms` indicator estimates how
much of the recent receive-side serial line time was unavailable when allowing
one complete UART character of quiet time before declaring idle.

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
can still make those bytes reappear as RX. `IDLE` is usable silence after the
RX idle qualification interval, rather than proof of electrical bus idle.

## Calculation

The number of wire bits used by one received character is calculated from the
serial settings that were actually applied when the connection opened:

```text
bits per character = 1 start bit
                   + configured data bits
                   + parity bit (0 for None, otherwise 1)
                   + configured stop bits (1, 1.5, or 2)
```

The complete character duration sets both the estimated on-wire time and the
minimum idle qualification interval:

```text
character time = bits per character ÷ baud
busy interval  = estimated start of an RX burst .. end + one character time
RX BUSY        = duration of the union of busy intervals ÷ observed time × 100
IDLE           = 100% - RX BUSY
```

For `115200 bps, 8N1`, one complete character takes approximately **86.806 µs**.
Gaps shorter than that stay busy. For a longer gap, its first 86.806 µs qualifies
the idle state; only the remaining silence contributes to IDLE. This threshold
is independent of the HEX display grouping timeout. It is an operational
definition of usable idle space, not a claim that UART requires an extra
character gap to decode consecutive bytes: frames can follow immediately after
the preceding stop bit, as described in the
[Microchip USART framing documentation](https://onlinedocs.microchip.com/oxy/GUID-A52628F4-6F6F-4C77-80CB-113A0C62DB75-en-US-10/GUID-55EAB72C-60B7-4EDA-AAD1-A65D7344E8BC.html).

For example, at `1000 bps, 8N1`, each byte occupies 10 ms:

| Traffic during one second | Byte-only occupancy | RX BUSY with idle qualification |
| --- | --- | --- |
| 50 contiguous bytes, then silence | 50% | 51% (one trailing 10 ms qualification) |
| 50 single bytes with 10 ms gaps | 50% | 100% (no silence remains after qualification) |
| 25 single bytes with 30 ms gaps | 25% | 50% (10 ms qualification, then 20 ms idle per gap) |

The receive observer queues only timestamps, lengths and native idle-boundary
metadata. No payloads are retained. Contiguous wire intervals coalesce in O(1)
without redistributing bytes across gaps or overlaps. The UI status batch drains
a double-buffered queue bounded to 4096 intervals per buffer. Any portion after
the sample timestamp remains in a separate bounded queue for the next sample;
byte counts split in the same proportion as the wire duration. The receive path
does not parse logs, write files or update controls for this meter.

BUSY retains the exact union of up to 65536 disjoint spans in the rolling minute.
An interval delivered late can fill earlier idle space without counting an
existing overlap twice, even inside one millisecond. UI sample cadence does not
change that union. Byte-count history uses 1 ms buckets with fractional values;
only partial byte-count buckets at window boundaries require interpolation.
If a timing queue saturates, its final intervals merge conservatively. If the
busy history saturates, its oldest quarter merges into a conservative envelope.
The tooltip reports that some gaps were counted as busy. Queue and history
memory remain bounded even if the UI stops sampling temporarily.

A native idle-terminated read completes after the configured idle timeout.
That known wait is subtracted from its completion timestamp before estimating
the wire interval, so driver timeout silence is not added to busy time.

Once a complete minute is available, `PEAK` divides that rolling minute into 60
fixed one-second buckets using the same busy timeline. A short first sample
immediately after Connect, HEX entry, or Clear cannot become a false full-second
peak, and a delayed UI timer does not spread traffic uniformly across seconds.

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

Overlapping inferred busy intervals count only once; `RX BUSY` stays within
0–100%, including with MOCK traffic or unusual driver/timestamp behavior.

## Measurement Limits

- Windows serial APIs do not expose a continuous electrical busy/idle signal.
  This is a receive-timing estimate, not an oscilloscope measurement.
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
- USB-UART and Windows driver buffering can hide individual byte times and gaps
  inside a delivered chunk. A chunk's wire start is estimated from its length
  and framing; unseen internal gaps cannot be reconstructed. Short-gap handling
  applies to the timing boundaries the driver actually exposes.
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
