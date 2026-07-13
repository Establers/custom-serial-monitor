# HEX RX idle-gap framing tests

HEX RX packet boundaries are variable-length and timeout-only. Tests must never
assume an 11-byte packet, a header, a delimiter, or a baud-specific timeout.

## Automated tests

Run both maintained test projects:

```powershell
dotnet test SerialMonitor.Core.Tests\SerialMonitor.Core.Tests.csproj -c Release
dotnet test SerialMonitor.WinUI.Tests\SerialMonitor.WinUI.Tests.csproj -c Release -p:Platform=x64
```

`SerialMonitor.Core.Tests` verifies:

- the configured timeout is used unchanged;
- boundaries are created at `gap >= timeout`;
- variable packet sizes from 1 byte through hundreds of kilobytes;
- delayed application transport chunks below the configured timeout, without
  treating those synthetic chunks as physical UART idle inside a packet;
- deterministic 1,000-packet randomized scenarios;
- serial frame-time recommendations without automatically applying them.

`SerialMonitor.WinUI.Tests` verifies:

- real `LogPipeline` grouping and output terminators;
- multiple native read completions remain separate even when they accumulate
  in RJCP's managed buffer before the consumer runs;
- byte conservation through pipeline input, HEX acceptance, and HEX emission;
- packets larger than the 64 KiB streaming segment remain one logical line;
- RJCP receives the exact profile timeout in HEX mode;
- terminal mode retains immediate transport draining;
- a driver line-status error prevents the associated short native completion
  from being treated as proven idle;
- HEX text remains an exact byte representation even if the saved terminal
  character encoding is UTF-8, CP949, or ASCII;
- bounded bridge queues apply backpressure and preserve order instead of
  dropping writes when temporarily full.

## Native-boundary implementation invariant

Do not reconstruct HEX packet boundaries by draining `SerialPortStream.Read()`
or `BytesToRead`. Those public APIs expose RJCP's accumulated managed buffer and
do not retain individual Win32 `ReadFile` completions.

`BoundaryPreservingSerialPortStream` subscribes to the protected
`NativeSerial.DataReceived` event, which RJCP 3.0.5 raises synchronously once per
native read completion before its public `DataReceived` event is coalesced. The
callback records only bounded boundary metadata. The dedicated receiver thread
then consumes exactly the recorded byte count; it remains responsible for all
byte copying and channel publication.

RJCP obtains `Frame`, `Parity`, and `Overrun` from Windows `ClearCommError`.
They are genuine driver-reported line-status signals, but they are not
independent proof that an analyzer observed a physical UART error. USB-UART
adapter firmware and drivers can report signals that the analyzer does not see.
When a native line-status signal coincides with a short read completion, that
completion is not promoted to an immediate HEX idle boundary. The application
profile timeout remains the conservative fallback.

Revalidate this invariant against RJCP source and the accumulated-completion
tests before changing the RJCP package version or replacing the receive stream.

## Hardware acceptance matrix

Use a hardware transmitter with stable timing. A Windows sender is not suitable
as the timing reference for millisecond gaps. Capture the same line with the
packet analyzer and record the adapter model and driver version.

The device packets in this acceptance matrix must transmit every byte
continuously. No intentional idle is inserted inside a packet; all listed idle
times are strictly between complete packets.

| Baud | Packet lengths | On-wire packet bytes | Inter-packet idle | Minimum packets |
| --- | --- | --- | --- | ---: |
| 9600 | fixed 11 B | continuous | 4 ms | 10,000 |
| 9600 | random 1-255 B | continuous | 2/3/4/5/10 ms | 10,000 per gap |
| 9600 | random 1-4096 B | continuous | 20/50/100/500 ms | 10,000 per gap |
| 38400 | fixed 11 B | continuous | 4 ms | 10,000 |
| 38400 | random 1-255 B | continuous | 1/2/3/4/5/10 ms | 10,000 per gap |
| 38400 | random 1-4096 B | continuous | 20/50/100/500 ms | 10,000 per gap |
| both | random 1-255 B | continuous; framing-error run | 4/10 ms | 10,000 |

For every run, choose the profile timeout above the continuous UART character
arrival interval (plus practical timing tolerance) and below the shortest idle
between complete packets. For example, 9600 8N1 is about 1.042 ms per byte, so
2 ms is a valid starting value when the measured inter-packet idle is 4 ms.

## Acceptance criteria

- Analyzer packet count equals HEX logical-line count when no serial errors are
  reported.
- Analyzer byte count equals SerialService RX bytes and LogPipeline processed
  bytes.
- `HEX accepted - HEX emitted - HEX pending` remains zero.
- `Frame` and `Parity` errors are recorded but never directly force a boundary.
- The last driver signal records nearby RX byte/chunk counters so analyzer
  captures can be correlated without interpreting the payload.
- Any `Overrun` invalidates the no-loss result because RJCP reports an actual
  driver overflow. In the pinned RJCP 3.0.5 API, `RXOver` means the driver
  receive buffer reached its 80% warning threshold; investigate byte counters
  and sustained buffer pressure, but do not treat that warning alone as proof
  that bytes were lost.
- No RX, file, or UI drop counters increase.
- No background exception is reported.

## Known physical limit

The analyzer observes the UART line. A USB-UART adapter may combine several UART
bursts before Windows receives them. If the adapter/driver does not preserve an
idle gap, software cannot reconstruct that lost timing. Always repeat a failed
case with the adapter model and driver version recorded.

## Raw bridge acceptance

Test both directions with pseudo-random binary payloads containing every byte
value, especially `00`, `7F`, `80`, `FE`, and `FF`. Compare SHA-256 hashes and
byte counts at both endpoints. Run once at a steady rate and once in bursts
large enough to create queue pressure. During an active run:

- source and destination byte streams must be identical and ordered;
- bridge dropped chunk/byte counters must remain zero;
- queue pressure may delay the serial receive worker but must not silently
  replace or discard an accepted bridge chunk;
- UI/parser drops in raw-bridge-priority mode do not imply a bridge-byte drop;
  the bridge's own counters are authoritative.
