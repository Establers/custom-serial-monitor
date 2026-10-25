# Optional local MCP

Prefer CLI when a local shell is available and no MCP connection exists. If local
MCP tools are already connected or explicitly preferred, use them directly rather
than spawning the CLI for every call. The target selection, evidence and no-blind-TX-
retry rules in SKILL.md apply equally. Discover actual tool names; clients may prefix
the server name.

| CLI | MCP tool | Relevant arguments |
| --- | --- | --- |
| `instances` | `serial_list_instances` | none |
| `status` | `serial_get_status` | `instanceId` |
| `ports` | `serial_list_ports` | `instanceId` |
| `commands` | `serial_list_commands` | `instanceId` |
| `connect` | `serial_connect` | `instanceId`, `port`, `baudRate` |
| `disconnect` | `serial_disconnect` | `instanceId` |
| `request` | `serial_send_and_capture` | `instanceId`, `port`, `command`, `txMode`, `lineEnding`, `durationMs`, `previewBytes` |
| `send` | `serial_send` | `instanceId`, `port`, `command`, `txMode`, `lineEnding`, `previewBytes` |
| `capture` | `serial_capture_rx` | `instanceId`, `port`, `durationMs`, `previewBytes` |
| `read` | `serial_read_capture` | `instanceId`, `captureId`, `offset`, `limit`, `format` |
| `cancel` | `serial_cancel_capture` | `instanceId` |

Defaults and result meanings match the CLI reference. There is no MCP `text-file`
argument: pass the actual text in `command`. Keep each capture below the client's
tool-call timeout, or configure a larger timeout before a longer capture. The server
allows requests up to 90 seconds; capture windows are at most 60 seconds.

## Registration when requested

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
accepts `--timeout 90000` when adding its server. The GUI's MCP guide can generate
client-specific registration/configuration text, including IDE surfaces.
Codex/Claude/Copilot configurations are not identical: for example VS Code Copilot
uses `servers`, whereas the CLI's MCP JSON uses `mcpServers`.

Stdio starts `serialctl mcp` as a local child; it still requires the GUI to be running.
An app needs a local MCP executor or local shell to use this Windows integration.
Do not expose a local endpoint publicly, invent HTTP support, or treat cloud-hosted
localhost as the user's Windows machine.
