# Installing the shared skill

This complete folder (SKILL.md, references, scripts, agents) is the portable unit.
No Claude-only frontmatter, tool pre-approvals or mandatory MCP dependency is used.
Install the same files into each host's supported discovery location.

Use the separate `SerialMonitorSkills.zip` download when the application is already
installed. Extract it and run its bundled `skills/Install-Skill.ps1`, pointing
`-CliPath` at the existing application's `tools/serialctl.exe`. The ZIP includes
installation guides and the complete skill; application/CLI binaries are installed
separately. For manual installation, copy its `skills/serial-monitor-control` folder
into one of the locations below and set `SERIAL_MONITOR_CLI` if needed.

| Host | Project location | Personal location |
| --- | --- | --- |
| Codex CLI / local app / IDE | `.agents/skills/serial-monitor-control` | `~/.agents/skills/serial-monitor-control` |
| Claude Code CLI / local app integration | `.claude/skills/serial-monitor-control` | `~/.claude/skills/serial-monitor-control` |
| GitHub Copilot CLI / supported IDE agent | `.github/skills/serial-monitor-control` | `~/.copilot/skills/serial-monitor-control` |

Copilot also recognizes `.agents/skills` and `.claude/skills` in projects; the installer
uses its dedicated `.github` location so each host has a predictable entry. Installed
duplicates have the same instructions. Do not edit generated copies: edit the source
and reinstall, or deliberately maintain a custom copy under a different name.

From the extracted `SerialMonitorSkills` folder (or a source checkout):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\skills\Install-Skill.ps1 -Client All -Scope Project -RootDirectory 'C:\work\firmware' -CliPath 'C:\SerialMonitor\tools\serialctl.exe'
powershell -NoProfile -ExecutionPolicy Bypass -File .\skills\Install-Skill.ps1 -Client All -Scope User -CliPath 'C:\SerialMonitor\tools\serialctl.exe'
```

ExecutionPolicy applies only to that child process; no persistent machine/user
policy is changed, and enforced organizational policy still takes precedence.

Project scope is the default. For user scope, RootDirectory defaults to the current
user's profile; for project scope it defaults to the current working directory.
The installer copies only this named skill and saves an optional local executable
path. It leaves client permissions/MCP/global instructions unchanged. A manifest
tracks installed file hashes: modified or unmanaged copies require explicit `-Force`
to overwrite. `-WhatIf` previews targets without changing them.

Codex invocation: `$serial-monitor-control` (or its skills picker).
Claude Code / Copilot invocation: `/serial-monitor-control`, when offered by the host.
Natural requests can also select the skill through its description. Reload skills
or restart the session if a host does not discover the new folder immediately.

Local discovery does not mean every cloud/app surface loads personal skill folders.
For an app that imports skill archives, import the complete folder/ZIP through that
app's supported mechanism. It still needs execution on the Windows PC for actual RX;
a cloud-only chat cannot control the port by loading instructions alone.

Official documentation checked 2026-10-25:

- [Codex skill discovery](https://learn.chatgpt.com/docs/build-skills)
- [Claude Code skills and local/cloud scopes](https://code.claude.com/docs/en/skills)
- [GitHub Copilot CLI skill locations](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills)
