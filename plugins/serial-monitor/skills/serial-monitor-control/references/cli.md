# CLI contract

Examples below use PowerShell. Resolve the skill's actual directory and executable
once; pass arguments as separate arguments, not a generated shell command string.

```powershell
$cli = powershell -NoProfile -ExecutionPolicy Bypass -File '<skill-directory>/scripts/Find-SerialCtl.ps1'
& $cli describe --json
& $cli instances
# Set $instance to the actual instanceId from the response, after choosing the target.
& $cli status --instance $instance
& $cli mode --instance $instance
& $cli ports --instance $instance
& $cli commands --instance $instance
& $cli connect --port COM7 --baud 115200 --instance $instance
& $cli configure --port COM7 --baud 57600 --byte-timeout-ms 60 --instance $instance
& $cli request --port COM7 --instance $instance --text 'status' --ending crlf --duration-ms 2000 --preview-bytes 4096
& $cli request --port COM7 --instance $instance --hex 'AA 01 FF' --duration-ms 1000 --preview-bytes 4096 --format hex
& $cli capture --port COM7 --instance $instance --duration-ms 1000 --preview-bytes 4096
& $cli read --instance $instance --capture $captureId --offset $nextOffset --limit 4096
& $cli read --instance $instance --capture $captureId --offset 0 --limit 4096 --format hex
```

`describe` (optionally `--json`) returns the standard `success`/`data` JSON envelope
without opening a GUI connection or COM port. `data` includes `schemaVersion`,
`controlApiVersion`, `cliVersion`, `minimumPluginVersion`, `features`, and current
MCP tool definitions in `tools`. Use it to discover the installed CLI's capabilities
before relying on a new feature. Its result describes the CLI/server, not the version
of a separately running GUI. For older CLIs that have no `describe`, inspect `help`
and stay within its documented commands.

COM7 and `status` are syntax examples, not a device specification. Use actual target
values and documented commands. `connect` reuses a matching connection and rejects
replacing another target. `disconnect` explicitly closes the GUI's connection.
`send` writes once without waiting; use `request` for response inspection.
`cancel` stops the current capture. Both accept `--instance`.

For a command containing quotes, dollar signs, backticks or newlines, create a
UTF-8 file with a file-writing tool, then:

```powershell
& $cli request --instance $instance --port COM7 --text-file '<absolute-command-file>' --ending none --duration-ms 2000 --preview-bytes 4096
```

The file is read as data, never evaluated as PowerShell. It must contain 1..16384
characters. Newlines in the file remain; do not accidentally append a second ending.
Plain text TX is UTF-8. Valid endings are `none`, `cr`, `lf`, `crlf` (default).
HEX ignores ending selection. Capture duration is 0..60000 ms (default 2000).
Preview size is 4..65536 bytes, default 16384. It changes only the first returned page.
`send`, `request` and `capture` accept `--format text|hex` for that page. HEX TX
defaults to a hex preview; otherwise the default is text. `read --format` remains
independent, so keep raw and decoded-text offsets separate.

## Live baud rate and byte timeout

```powershell
& $cli status --instance $instance
& $cli configure --port COM7 --baud 57600 --instance $instance
& $cli configure --port COM7 --byte-timeout-ms 80 --instance $instance
& $cli configure --port COM7 --baud 115200 --byte-timeout-ms 40 --instance $instance
```

Supply at least one of `--baud` (1..10000000 BPS) or `--byte-timeout-ms`
(1..5000 ms). Custom baud rates are allowed if the device/driver supports them;
an unsupported rate fails. The current `status` includes `baudRate` and
`byteTimeoutMs`. Changing BPS updates the host's existing serial handle/session;
it does not transmit a device configuration command. Use the documented product
procedure to coordinate both sides and verify actual RX after the transition.
An optional `--port` asserts the expected target, rather than changing ports.
The operation requires an existing connection, including timeout-only changes;
it does not connect/reconnect. Use the GUI/profile to configure offline settings.
Configuration is exclusive and returns `busy` during capture, TX, sequences,
bridge activity or another control operation. Let that work finish first.

Byte timeout changes the live HEX display's idle-gap grouping (default 40 ms).
It does not set the total response wait; `--duration-ms`/step `captureMs` do that.
Theoretical per-byte or RS485 gap calculations do not include Windows/USB adapter
buffering and scheduling/processing jitter. Start with empirical slack and observe
the actual device: increase the value for unintended splits, or reduce it cautiously
for merged exchanges. No timeout guarantees protocol packet boundaries; validate
headers, lengths and CRC/checksum from the product specification when required.
Raw captures preserve ordered bytes and expectation matching ignores receive chunk
and display-group boundaries, including split responses or multiple packets in a
single chunk. Responses arriving after the chosen capture window remain outside
that step's evidence.

## GUI Terminal / HEX mode

`status` and `mode` query the current GUI state; the status `mode` field is
`terminal` or `hex`. To change it for the selected GUI:

```powershell
& $cli mode --set hex --instance $instance
& $cli mode --set terminal --instance $instance
```

Terminal displays decoded text in a terminal; HEX displays original bytes in
hexadecimal. The setting also changes GUI manual/sequence TX interpretation and
the active rule mode. An actual mode switch clears visible logs and search results;
saved logs and raw RX captures remain intact. It neither connects nor sends data.

This setting does not determine an automation command's TX format: `--text` and
`--text-file` still send text in either GUI mode, and `--hex` still sends exact bytes.
Likewise `read --format hex` selects a capture page format, without changing the GUI.

Mode changes acquire exclusive control and return `busy` during AI control/capture,
GUI TX, sequences, bridge startup/activity, reconnect or another busy operation.
Let that work finish before changing the mode; mode requests support cancellation.
Use a GUI and CLI build that supports mode control.

## Register and run sequences

Use the same registered sequences as the GUI; `save`/`delete` persist its shared
profile and do not send device commands. Save one UTF-8 JSON definition (not a GUI
profile wrapper) in a task-local file. Syntax example only; replace command and
expectation from the actual device specification:

Register repeated/frequently used commands even when the sequence has only one
step. Choose an informative name such as a device/model plus the operation, inspect
existing definitions to avoid duplicates, and set explicit `txMode` for reusable
Terminal or HEX commands. Registration preserves content without replaying TX.

```json
{
  "name": "Device status check",
  "repeatCount": 1,
  "steps": [
    {
      "name": "Read status",
      "command": "status",
      "txMode": "text",
      "lineEnding": "crlf",
      "captureMs": 2000,
      "delayAfterMs": 300,
      "expect": { "kind": "text", "value": "abcd" }
    }
  ]
}
```

For a HEX packet, a step uses the same fields with explicit bytes and no ending:

```json
{
  "name": "Read protocol status",
  "command": "AA 01 FF",
  "txMode": "hex",
  "lineEnding": "none",
  "captureMs": 2000,
  "delayAfterMs": 300,
  "expect": { "kind": "hex", "value": "AA 81 00" }
}
```

These bytes are syntax examples, not valid commands/ACKs for an arbitrary device.
Use complete packets from the actual protocol, including its required CRC/checksum.
Omit `expect` if the protocol does not define a reliable literal response.

```powershell
& $cli sequence save --file '<absolute-sequence-json>' --instance $instance
& $cli sequence list --offset 0 --limit 100 --instance $instance
& $cli sequence get --name 'Device status check' --instance $instance
& $cli sequence run --name 'Device status check' --port COM7 --instance $instance
# Set $runId from the accepted start result, then inspect the same run.
& $cli sequence status --run $runId --instance $instance
& $cli sequence cancel --run $runId --instance $instance
& $cli sequence delete --name 'Device status check' --instance $instance
```

`list` returns metadata pages with `hasMore`/`nextOffset` (limit 1..100, default 100).
`get` returns a complete definition or rejects oversized legacy definitions; it
never truncates one. `save` rejects an existing name unless `--overwrite true` is
provided. Successful registration/deletion returns `persisted: true` after the
profile save. Registration itself grants no permission to run unrelated commands.

`run` returns a background `runId` promptly. **An accepted run continues after its
CLI/MCP request or client disconnects.** Normal `cancel` and MCP request cancellation
do not stop an accepted sequence. Use `sequence cancel --run $runId`, then inspect
status until execution and RX persistence finish. Omit `--run` from status to read
the latest run; if a start result is lost, inspect this before starting again.

Run state is `running`, `completed`, `canceled` or `failed`. `stepIndex` is zero-based
(-1 before the first step). Results retain each step's `sendStatus`, raw
`rxStartOffset`/`rxEndOffset`, `matchStatus`, and the whole-run `capture` once
available. Use its `captureId` with `read` to inspect all RX in bounded pages. Step
offsets refer to raw bytes: use `--format hex` for those offsets, never reuse them
as decoded text offsets. Read-only status does not wait for the complete run.

All steps validate before TX. Required root fields are `name`, `repeatCount`, `steps`.
Limits: definition <=48 KiB UTF-8 JSON; name 1..128 characters; 1..128 steps;
repeatCount 1..128; steps*repeatCount <=128;
command 1..16384 characters; optional step name <=128. Each `captureMs`/`delayAfterMs`
is 0..60000 / 0..600000 respectively (defaults 2000/300);
total sum(captureMs+delayAfterMs)*repeatCount <=600000
ms. Default `txMode` is `text` and `lineEnding` is `crlf`; HEX sends exact bytes.
New automation registrations are limited to 1000. Full-run capture is bounded to 64
MiB of raw RX. GUI mode does not change these explicit per-step TX modes.

`expect` is optional. `{kind:"text",value:"abcd"}` encodes the literal with the
GUI's RX encoding and searches for exact contiguous bytes; unrepresentable literals
are rejected before TX. Matching is case-sensitive.
`{kind:"hex",value:"AA 01 FF"}` searches exact contiguous bytes directly.
Value length is 1..4096 characters. It is never regex, fuzzy matching or subsequence
reconstruction. Results are `matched`, `not_matched`, `not_requested` or
`inconclusive`; capture gaps cannot establish a complete verdict. `ab12c3d4` is not
an exact match for `abcd`, but interleaving can be normal. Read the actual RX and
protocol before product judgment. A contiguous literal can cross receive chunks or
display groups and can occur among multiple coalesced packets. It cannot cross the
step's raw-byte observation range; extend `captureMs` for late responses.
A match never ends `captureMs` early, and a miss
does not skip later steps. Transport/capture failures and cancellation can stop
the run; uncertain TX is never retried. Expectations are evaluated only by CLI/MCP
automation runs, not when the GUI manually runs a sequence.

## Result and paging

Data commands print JSON. Exit 0 indicates successful handling and a complete capture
when present. Exit 1 can still return `data.capture` and `data.page` with useful RX:

```json
{
  "success": true,
  "data": {
    "capture": {
      "captureId": "a real 32-character capture ID",
      "sendStatus": "sent",
      "captureStatus": "completed",
      "receivedBytes": 52000,
      "storedBytes": 52000,
      "droppedBytes": 0,
      "rxGapDetected": false,
      "rawFile": "absolute .bin path",
      "textFile": "absolute UTF-8 .txt path"
    },
    "page": {
      "captureId": "the same capture ID",
      "format": "text",
      "text": "actual RX text",
      "nextOffset": 4096,
      "hasMore": true
    }
  }
}
```

Use real values returned by the CLI, not the illustrative JSON above. Additional
metadata includes target settings, receive session, UTC start/end, serial errors,
and original command/mode. `sendStatus` is `not_requested`, `not_sent`, `sent`, or
`unknown`. `captureStatus` can be `completed`, `canceled`, `disconnected`, `rx_gap`,
`size_limit`, `write_error`, or `send_error`. Error results include `error.code` and
`error.message`; inspect retained capture data before deciding next steps.

`read` returns `{capture, page}` under `data` too. Offset is a byte offset in the
selected text or raw file. Follow returned `nextOffset` instead of adding the limit:
UTF-8 pages stop at complete character boundaries. Keep text and hex cursors separate.
Capture reads preserve CR/LF and streaming decoder state. A partial character at
capture start can decode imperfectly; raw `.bin` / hex remains byte-exact.

The shell's output limit can truncate JSON before the agent sees it. Use a small
preview/page, sufficient shell output budget, or redirect stdout to a task-local file
and read that file in bounded pieces. A lost/truncated result never justifies resending.

## Recovery

| Code/status | Action |
| --- | --- |
| `gui_not_running` | Have the updated Windows GUI started; do not open COM directly. |
| `instance_required` | Inspect instances/status and choose the intended GUI. |
| `instance_not_found` | Re-discover the target; its launch ID changed. |
| `already_connected`, `connection_mismatch`, `port_mismatch` | Inspect actual target; do not send to a different device. |
| `busy` | Inspect/wait for current work within task scope. |
| `profile_conflict` | Another window changed the same sequence, or a safe comparison baseline is unavailable. Preserve the file, review/reload the latest profile and resolve the conflict. Do not blindly overwrite, retry or disconnect an active device to reload. |
| `canceled`, `disconnected`, `rx_gap`, `size_limit`, `write_error` | Inspect partial data/sendStatus. Product judgement may be uncertain. |
| `capture_not_found` | Retained capture expired or belongs to another instance; use a returned file path if available. |

Collection is independent of GUI pause/filter/Terminal/HEX display. Limits are 64 MiB
raw RX per capture, 1024 pending chunks/8 MiB queue. Retention normally keeps up to
32 captures per launch with a 512 MiB cleanup threshold; pinned files are skipped.
