# Result storage formats

These are internal disk formats, separate from CLI/MCP JSON response schemas.
V1 object-shaped results and 40-byte RX indexes remain readable. New writes use
v2. Existing files are not automatically converted. Keep the whole run directory
when copying results: compact rows reference `definition.json`, `summary.json`
and `steps.idx`.

## GUI sequence results

`steps.jsonl` contains one JSON array per completed attempt:

```text
[2, startDeltaTicks, durationTicks, rxStart, checkLength, rxLength, checkStatus, extras]
```

`startDeltaTicks` is relative to `summary.StartedAt.UtcTicks`. Durations and
relative timestamps use .NET ticks (100 ns). Byte lengths are relative to
`rxStart`. Check statuses are 1 matched, 2 not_matched, 3 not_requested,
4 canceled, 5 inconclusive, 6 a string stored as `extras.c`.

The existing 24-byte little-endian `steps.idx` entry contains the JSON byte
position (int64), byte length (int32), repeat (int32), check status (int32) and
original step number (int32). The row ID is its one-based position in this index.
Definition step numbers may be non-contiguous when editor rows are disabled.

| Optional extra | Meaning / default when absent |
| --- | --- |
| `a` | Attempt number / 1 |
| `s` | Send status / sent for TX, not_requested for RX Check |
| `state` | Step state / completed |
| `o`, `z` | Start/end timezone offset minutes / UTC, then start offset |
| `m` | Match start relative to rxStart and match byte length / no match |
| `ml` | Match length when match start is null / 0 |
| `ma`, `mo` | Match time relative to step start and offset / no match time, start offset |
| `r` | Related TX definition step / none |
| `rt`, `ro` | Related TX time relative to start and offset / no time, start offset |
| `ms` | Response milliseconds / null |
| `e` | Error / null |
| `p` | Base64 decoder prefix / null (empty and null remain distinct) |
| `x` | Named overrides for any immutable field differing from the definition |

Commands, TX hex, pattern, kind, timeout, TX mode and related TX command/hex
normally come from the definition, with the original preview bounds applied.
Overrides preserve null values and runtime deviations, including legacy attached
checks. Display text, time text and result labels are computed after loading.

## RX clock index

`rx-times-v2.idx` starts with a 32-byte header: ASCII `SRX2`, four reserved bytes,
monotonic anchor (int64), anchor UTC ticks (int64), source clock frequency (int64).
All numbers are little-endian. Each 24-byte entry contains:

| Offset | Value |
| --- | --- |
| 0 | Raw RX offset (uint32; the existing run limit is 1 GiB) |
| 4 | Chunk length (int32) |
| 8 | Original monotonic receive timestamp (int64) |
| 16 | Connection generation (int64) |

Wall time uses the same tick conversion as `Stopwatch.GetElapsedTime`, with the
recorded source frequency. Explicit lengths prevent unindexed raw tails from
being attributed to the last known timestamp after interrupted writes. Readers
ignore partial final entries, binary-search by offset, and still return at most
16 KiB / 200 original receive chunks. Old `rx-times.idx` files use the v1 reader.

## CLI/MCP capture metadata

`<captureId>.json` stores:

```text
[2, startUtcTicks, durationTicks, port, generation, requestedMs, receivedBytes, extras]
```

ID and normal `.bin`/`.txt` paths derive from the file name. Extras retain command,
non-default capture/send status, stored bytes when different from received bytes,
nonzero dropped-byte/error counts, RX-gap flag, error, non-default TX mode/ending,
preview format, timezone offsets and unusual file paths. Missing capture status
means completed; missing send status means not_requested. No public fields are
removed: `GetResult` restores the complete `CaptureResult` before the controller
serializes CLI/MCP responses.

MCP retains full text and structured content by default for client compatibility.
`serialctl mcp --tool-content summary` sends the full response once in
`structuredContent` with a short success/error text for modern structured clients.
Older protocols always retain full JSON text, even when that option is requested.
