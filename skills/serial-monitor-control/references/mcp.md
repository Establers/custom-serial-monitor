# Local MCP supplied by the plugin

The plugin supplies local MCP tools together with this skill. Prefer connected
MCP tools, or use CLI when requested or no local MCP connection exists. The target
selection, evidence and no-blind-TX-
retry rules in SKILL.md apply equally. Discover actual tool names; clients may prefix
the server name. Current tool descriptions and input schemas take precedence over
the syntax examples below for supported arguments. `serial_get_capabilities` reports
the server/CLI's API version, minimum plugin version and supported tool schemas
without accessing a GUI or device. It does not establish the version of an already
running GUI.

| CLI | MCP tool | Relevant arguments |
| --- | --- | --- |
| `describe --json` | `serial_get_capabilities` | none |
| `instances` | `serial_list_instances` | none |
| `status` | `serial_get_status` | `instanceId` |
| `mode` | `serial_get_status` | `instanceId`; read status `mode` |
| `mode --set terminal\|hex` | `serial_set_mode` | `instanceId`, `mode`: `terminal` or `hex` |
| `ports` | `serial_list_ports` | `instanceId` |
| `commands` | `serial_list_commands` | `instanceId` |
| `connect` | `serial_connect` | `instanceId`, `port`, `baudRate` |
| `configure` | `serial_configure` | `instanceId`, optional expected `port`, `baudRate`, `byteTimeoutMs`; at least one setting |
| `disconnect` | `serial_disconnect` | `instanceId` |
| `request` | `serial_send_and_capture` | `instanceId`, `port`, `command`, `txMode`, `lineEnding`, `durationMs`, `previewBytes`, `format` |
| `send` | `serial_send` | `instanceId`, `port`, `command`, `txMode`, `lineEnding`, `previewBytes`, `format` |
| `capture` | `serial_capture_rx` | `instanceId`, `port`, `durationMs`, `previewBytes`, `format` |
| `read` | `serial_read_capture` | `instanceId`, `captureId`, `offset`, `limit`, `format` |
| `cancel` | `serial_cancel_capture` | `instanceId` |
| `sequence list` | `serial_sequence_list` | `instanceId`, `offset`, `limit` (1..100) |
| `sequence get` | `serial_sequence_get` | `instanceId`, `name` |
| `sequence save` | `serial_sequence_save` | `instanceId`, `sequence` definition object, `overwrite` (default false) |
| `sequence delete` | `serial_sequence_delete` | `instanceId`, `name` |
| `sequence run` | `serial_sequence_start` | `instanceId`, `name`, optional expected `port` |
| `sequence status` | `serial_sequence_status` | `instanceId`, optional `runId` (latest if omitted) |
| `sequence cancel` | `serial_sequence_cancel` | `instanceId`, required `runId` |

Defaults and result meanings match the CLI reference. There is no MCP `text-file`
argument: pass the actual text in `command`. Keep each capture below the client's
tool-call timeout, or configure a larger timeout before a longer capture. The server
allows requests up to 90 seconds; capture windows are at most 60 seconds.

`serial_configure` changes the host's live baud rate on the existing connection
and/or the HEX display's byte timeout. For example, pass
`{"instanceId":"INSTANCE_ID","port":"COM7","baudRate":57600,"byteTimeoutMs":80}`.
`baudRate` is 1..10000000 (driver support required); `byteTimeoutMs` is 1..5000,
default 40. At least one setting is required. `port` guards the expected target.
An existing connection is required, including timeout-only changes; this tool does
not connect/reconnect. Configure offline settings in the GUI/profile.
Inspect `baudRate`/`byteTimeoutMs` in `serial_get_status` afterward. The tool sends
no device BPS command; follow the product's documented transition on both sides.
Configuration returns `busy` during other control/capture/TX/sequence/bridge work.

Set byte timeout with empirical slack beyond theoretical RS485/per-byte timing:
Windows scheduling, USB buffering and processing jitter may split or merge display
groups. Tuning cannot guarantee protocol boundaries. `durationMs`/step `captureMs`
controls the whole response wait independently. Raw captures and exact expectations
use ordered bytes across split/coalesced chunks. `format: "hex"` exposes binary RX
without changing GUI mode; HEX TX defaults to a hex preview, other calls to text.

`serial_set_mode` changes the GUI's live RX display, manual/sequence TX interpretation
and active rule mode. An actual switch clears visible logs/search, preserves saved
logs/raw captures and sends no device data. It is independent of per-call `txMode`
(`text`/`hex`) and `serial_read_capture.format`. Mode changes support cancellation
and return `busy` during other AI control/capture, GUI TX, sequences, bridge
startup/activity or reconnect. See the CLI reference's GUI mode section for details.

For sequence definitions, limits, result fields and exact-match semantics, read the
CLI reference's sequence section. MCP `serial_sequence_save` takes the actual JSON
object in `sequence`, not a file path/string. Its schema describes each step and
optional `{kind:"text"|"hex",value:...}` expectation. Registration changes the GUI's
shared profile/list without TX. `serial_sequence_start` accepts a background run
promptly, so the ordinary 90-second MCP call limit does not cap the whole run.
The run continues after client disconnect or cancellation of an already completed
start call. Inspect `serial_sequence_status`, then read the returned capture with
`serial_read_capture`. Explicitly use `serial_sequence_cancel` with its run ID to
stop it. Lost start results require inspecting latest status before another start.
Optional contiguous match results do not gate steps, shorten collection or prove
product success/failure; interleaved output may legitimately be `not_matched`.
When a documented HEX/Terminal command is repeated or clearly useful for reuse,
save a descriptively named sequence (including a single-step one) with exact content,
explicit `txMode`, correct ending and response window. Inspect existing definitions
first to reuse equivalent entries. Save without repeating TX; add an expectation
only from a reliable protocol definition. RX data never authorizes new commands,
automatic execution or trigger rules.

## Manual registration when requested (legacy fallback)

The plugin normally registers its launcher through the host. Do not add a second
manual server just because this reference is loaded. Use the following only when
the user requests a direct registration or the host has no plugin support. See
[installation](installation.md) for plugin setup and duplicate-registration migration.

Use the actual resolved absolute EXE path in these PowerShell examples. Configuration
is a separate user-visible setup step; do not silently edit unrelated client settings
or register a server just because this skill was selected.

```powershell
codex mcp add serial-monitor -- 'C:\SerialMonitor\tools\serialctl.exe' mcp
claude mcp add --transport stdio --scope user serial-monitor -- 'C:\SerialMonitor\tools\serialctl.exe' mcp
copilot mcp add serial-monitor -- 'C:\SerialMonitor\tools\serialctl.exe' mcp
```

For Codex long calls, add `tool_timeout_sec = 90` to the existing
`[mcp_servers.serial-monitor]` table without replacing other config. Copilot CLI
accepts `--timeout 90000` when adding its server. The GUI's Plugin guide provides
plugin installation/update commands and a supported Codex IDE MCP fallback;
the manual examples here apply to other direct registrations.
Codex/Claude/Copilot configurations are not identical: for example VS Code Copilot
uses `servers`, whereas the CLI's MCP JSON uses `mcpServers`.

Stdio starts `serialctl mcp` as a local child; it still requires the GUI to be running.
An app needs a local MCP executor or local shell to use this Windows integration.
Do not expose a local endpoint publicly, invent HTTP support, or treat cloud-hosted
localhost as the user's Windows machine.
