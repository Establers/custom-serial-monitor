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

The application setup EXE selects only the application's installation directory.
Install this skill separately from the skill ZIP when wanted. Application upgrades
leave existing skills unchanged; rerun a new skill ZIP's script when instructions
change, using the same client/scope/destination settings.

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
When user scope omits `-RootDirectory`, existing `CODEX_HOME`, `CLAUDE_CONFIG_DIR`
and `COPILOT_HOME` select their respective configuration roots. For Codex, this
installer proposes `<CODEX_HOME>/skills/serial-monitor-control` as the initial path
when `CODEX_HOME` is set; otherwise the proposed personal destination is
`~/.agents/skills/serial-monitor-control`. Confirm discovery in the actual host;
the root choice does not make every arbitrary folder discoverable.
Claude skills live at `<CLAUDE_CONFIG_DIR>/skills/serial-monitor-control`;
Copilot CLI skills at `<COPILOT_HOME>/skills/serial-monitor-control`. Explicit
`-RootDirectory` uses the standard client subfolders below that root.

Override individual final destinations with `-CodexSkillDirectory`,
`-ClaudeSkillDirectory`, `-CopilotSkillDirectory` (absolute paths). These take
precedence over scope/root and receive `SKILL.md` directly. For example:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\skills\Install-Skill.ps1 -Client Claude -Scope User -CliPath 'D:\Apps\SerialMonitor\tools\serialctl.exe' -ClaudeSkillDirectory 'D:\AI\claude\skills\serial-monitor-control'
```

Use a destination the AI host already searches. An arbitrary folder selection
cannot force host discovery; this installer does not set configuration-root
environment variables. The above custom Claude example assumes the active Claude
configuration root is already `D:\AI\claude`.
Choose a new/empty folder or an existing managed skill folder; nonempty unmanaged
destinations are rejected unless replacement is explicitly requested with `-Force`.

The installer copies only this named skill and saves an optional local executable
path. It leaves client permissions/MCP/global instructions unchanged. A manifest
tracks installed file hashes: modified or unmanaged copies require explicit `-Force`
to overwrite. `-WhatIf` previews targets without changing them.

Moving the application requires repinning the skill manually. For an application
installed at `D:\Apps\SerialMonitor`, rerun the script with
`-CliPath 'D:\Apps\SerialMonitor\tools\serialctl.exe'` and the same destination
options, or set `SERIAL_MONITOR_CLI` in the AI host's environment to that executable.
Existing MCP registrations keep their previous command: generate the new actual
path in the app's MCP tab and replace that registration when needed. Uninstalling
the application leaves external AI skill folders intact.

Codex invocation: `$serial-monitor-control` (or its skills picker).
Claude Code / Copilot invocation: `/serial-monitor-control`, when offered by the host.
Natural requests can also select the skill through its description. Reload skills
or restart the session if a host does not discover the new folder immediately.

Local discovery does not mean every cloud/app surface loads personal skill folders.
For an app that imports skill archives, import the complete folder/ZIP through that
app's supported mechanism. It still needs execution on the Windows PC for actual RX;
a cloud-only chat cannot control the port by loading instructions alone.

Official skill locations and custom roots:

- [Codex skill discovery](https://learn.chatgpt.com/docs/build-skills)
- [Codex custom configuration root](https://learn.chatgpt.com/docs/config-file/environment-variables)
- [Codex skill path example](https://developers.openai.com/cookbook/examples/codex/iterating-development-workflows-with-codex)
- [Claude Code skills and local/cloud scopes](https://code.claude.com/docs/en/skills)
- [Claude Code configuration-directory override](https://code.claude.com/docs/en/claude-directory)
- [GitHub Copilot CLI skill locations](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills)
- [GitHub Copilot CLI configuration-directory override](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-config-dir-reference)

Skill locations and custom roots checked 2026-10-26; host-specific discovery and
organization policy still apply.
