# Serial Monitor plugin

This plugin contains the shared `serial-monitor-control` skill and a local Windows
MCP launcher. Install the Serial Monitor application separately (1.6.0 or later).
Keep the GUI running while using its serial tools.

Plugin identity: `serial-monitor@serial-monitor-tools`.
Git marketplace: `Establers/custom-serial-monitor`.

The launcher discovers `tools/serialctl.exe` beside a bundled local marketplace,
from a uniquely located running GUI (including portable apps whose plugins were
cached elsewhere), through installer registration, or at the default per-user
install location. Several running GUI instances from one folder share one CLI;
different folders require explicit selection. `SERIAL_MONITOR_CLI` selects another
installation when the AI host passes that variable to its stdio MCP process.
Restart the AI host after changing its environment. Hosts that filter MCP
environment variables, including Codex CLI 0.164.0-alpha.11.1, need automatic
application discovery or manual MCP registration with an absolute executable path;
setting the variable in the parent shell alone is insufficient.
The launcher performs a bounded, read-only `serialctl describe --json` compatibility
check before starting MCP. It never opens a port, starts the GUI or sends device
commands during discovery.

The root portable manifest and `mcp.json` serve Codex and Copilot; the
`.claude-plugin` manifest and `.mcp.json` serve Claude Code. No install hook changes
global instructions, permissions, environment variables or client configuration.

`mcp.codex.json` is only for the legacy Codex manifest fallback. Its 90-second
timeout does not configure modern portable-plugin clients. Keep collection windows
within the active AI host's timeout, or change that host's supported timeout setting.

The canonical skill is maintained at `skills/serial-monitor-control` in the source
repository. Run `scripts/package_agent_plugin.ps1 -SyncOnly` after changing that
source, and include the generated plugin skill copy in the same commit. CI checks
that the copy is in sync. Do not edit the generated copy here.

See the [plugin guide](https://github.com/Establers/custom-serial-monitor/blob/main/docs/agent_plugins.md)
for installation, updates, migration from standalone skills/MCP, and troubleshooting.
