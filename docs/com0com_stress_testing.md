# com0com Packet And Timeout Stress Testing

The in-app `MOCK` source is useful for parser, logging, event, and UI load tests,
but it writes directly to the application's receive channel. It does not pass
through com0com, Win32 `ReadFile`, or `ReadIntervalTimeout`. Use the physical COM
stress tool when validating the configured HEX idle timeout.

## Recommended Setup

- com0com pair: `COM4 <-> COM5`
- Serial Monitor: open `COM4`
- Stress tool: open `COM5`
- Serial format on both sides: 8 data bits, no parity, one stop bit, no handshake
- Recommended baud: `460800` or higher for the default offered load

At 115200 baud, 60-byte packets every 4 ms offer about 15,000 bytes/s while an
8N1 link carries only about 11,520 bytes/s. That is a valid overload test, but it
cannot preserve the requested timing at the wire. Use 460800 for a timeout test,
then repeat at 115200 specifically as a saturation/backpressure test.

## Two Serial Monitor Instances

This matches the existing two-program workflow and needs no console generator.

1. In the sender instance, connect to `MOCK`, start the raw bridge on `COM4`, and
   select `Visual HEX 3-5 ms` under Settings > Mock/Test > Pattern.
2. Start mock stress. The Lines/sec and Burst values do not apply to this timed
   pattern: it always emits 24-64 byte packets at random 3-5 ms short gaps and
   inserts a random 25-40 ms long gap after every 2-6 packets.
3. In the receiver instance, connect directly to `COM5` at the same baud, select
   HEX mode, and apply a 15 ms HEX timeout.
4. Confirm the 2-6 short-gap packets form one HEX group and each 25-40 ms idle
   period starts a new group. Watch the receiver's diagnostics and disk log for
   errors or missing data.

The sender's internal MOCK side still bypasses native COM timing, but the raw
bridge sends those bytes through `COM4 -> COM5`; therefore the receiver instance
does exercise its real Win32/native idle-timeout path.

### Reading The Visual HEX Frame

Each intended packet has this byte layout:

```text
AA 55 | ROLE | GROUP(4) | INDEX | COUNT | LENGTH | SEQUENCE(4) | FILL | 55 AA
```

- `ROLE=F1`: first packet of the intended group
- `ROLE=F2`: middle packet
- `ROLE=F3`: last packet
- `GROUP`: big-endian intended group number
- `INDEX/COUNT`: one-based packet index and total packets in that group

With a 15 ms timeout, one correct HEX log line must start with `AA 55 F1`, keep
the same `GROUP`, contain packet indexes `01` through `COUNT`, and finish with
the `F3` packet's `55 AA` footer.

- Unexpected split: a line ends on an `F1`/`F2` packet, or the next line starts
  with `F2`/`F3`.
- Unexpected merge: one line contains an `F3` packet followed by the next
  group's `AA 55 F1` packet.
- Correct boundary: every logical log line is exactly `F1 ... F2 ... F3` for one
  `GROUP`. A two-packet group is simply `F1 ... F3`.

The terminal may visually wrap a long HEX line. That is display wrapping, not a
packet split; a real split creates a new timestamped/logical log line.

## Dense 3-5 ms Load

Connect Serial Monitor to `COM4`, select Terminal or HEX mode as needed, then run
from the repository root:

```powershell
dotnet run --project SerialMonitor.StressTool -- --port COM5 --baud 460800 --duration 120
```

The default stream contains deterministic binary frames of 24-96 bytes with a
random 3-5 ms delay after each write. Every frame contains:

```text
SMST | int64 sequence | uint16 total length | int32 group | payload | CRC32
```

The console reports the actual observed packet-start gap and throughput at the
end of the run. Reuse the default seed to reproduce the same packet sizes and
payloads, or supply `--seed N`.

## HEX Idle-Timeout Probe

Set Serial Monitor to HEX mode and apply an idle timeout such as `15 ms`. Then
run:

```powershell
dotnet run --project SerialMonitor.StressTool -- --port COM5 --baud 460800 --mode timeout --duration 60 --min-gap-ms 3 --max-gap-ms 5 --min-group-gap-ms 32 --max-group-gap-ms 40
```

Expected behavior with a 15 ms timeout:

- frames separated by 3-5 ms remain in the same HEX group;
- each 32-40 ms gap closes the current group;
- the next frame starts a new group;
- RX and file-log byte counts continue without transport drops;
- serial frame/parity/overrun/RX-over and connection-error counters remain zero.

Use guard bands around the timeout for a reliable correctness test. For example,
with a 15 ms timeout, use short gaps no greater than 5 ms and long gaps at least
30 ms. A 14.9/15.1 ms split mostly measures Windows scheduling and USB/driver
jitter, so use that only as a separate exploratory boundary test.

To make the load harsher, increase packet size or reduce the short gap:

```powershell
dotnet run --project SerialMonitor.StressTool -- --port COM5 --baud 921600 --duration 300 --min-bytes 64 --max-bytes 256 --min-gap-ms 1 --max-gap-ms 5
```

Run `dotnet run --project SerialMonitor.StressTool -- --help` for every option.

## Automated Native-Boundary Check

Close Serial Monitor first because the test itself opens `COM4`. Ensure neither
COM endpoint is open in another program, then run:

```powershell
$env:SERIAL_COM0COM_STRESS_TEST = '1'
dotnet test SerialMonitor.WinUI.Tests\SerialMonitor.WinUI.Tests.csproj -p:Platform=x64 --filter FullyQualifiedName~Com0ComNativeIdleStressTests
Remove-Item Env:SERIAL_COM0COM_STRESS_TEST
```

This opt-in test sends 80 deterministic groups. Each group contains 2-8 packets
of 24-96 bytes separated by random 3-5 ms gaps; groups are separated by 32-40
ms. The Serial Monitor receive service opens `COM4` with an exact 15 ms native
idle timeout and asserts all of the following:

- every received byte matches in order;
- every expected group corresponds to exactly one native read completion;
- every completion is marked as an idle boundary;
- received byte/chunk counters match;
- serial and connection error counters remain zero.

The hardware-dependent body does not run unless `SERIAL_COM0COM_STRESS_TEST=1`,
so normal CI and local unit-test runs do not depend on com0com.
