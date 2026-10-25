# CLI contract

Examples below use PowerShell. Resolve the skill's actual directory and executable
once; pass arguments as separate arguments, not a generated shell command string.

```powershell
$cli = powershell -NoProfile -ExecutionPolicy Bypass -File '<skill-directory>/scripts/Find-SerialCtl.ps1'
& $cli instances
# Set $instance to the actual instanceId from the response, after choosing the target.
& $cli status --instance $instance
& $cli ports --instance $instance
& $cli commands --instance $instance
& $cli connect --port COM7 --baud 115200 --instance $instance
& $cli request --port COM7 --instance $instance --text 'status' --ending crlf --duration-ms 2000 --preview-bytes 4096
& $cli request --port COM7 --instance $instance --hex 'AA 01 FF' --duration-ms 1000 --preview-bytes 4096
& $cli capture --port COM7 --instance $instance --duration-ms 1000 --preview-bytes 4096
& $cli read --instance $instance --capture $captureId --offset $nextOffset --limit 4096
& $cli read --instance $instance --capture $captureId --offset 0 --limit 4096 --format hex
```

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
| `canceled`, `disconnected`, `rx_gap`, `size_limit`, `write_error` | Inspect partial data/sendStatus. Product judgement may be uncertain. |
| `capture_not_found` | Retained capture expired or belongs to another instance; use a returned file path if available. |

Collection is independent of GUI pause/filter/Terminal/HEX display. Limits are 64 MiB
raw RX per capture, 1024 pending chunks/8 MiB queue. Retention normally keeps up to
32 captures per launch with a 512 MiB cleanup threshold; pinned files are skipped.
