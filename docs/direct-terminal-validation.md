# Direct terminal input and RX rendering

Direct input uses xterm keystrokes with no added line ending, and a separate,
generation-bound raw RX display buffer. The normal `LogPipeline` still receives
the same chunks and continues parsing, rule detection and asynchronous file
logging. Its escaped log text is not sent to the interactive screen.

Enabling direct input starts a fresh device screen. Disabling it restores the
retained normal log snapshot. `CR`, `BS`, ANSI cursor/erase sequences and partial
prompts stay intact in the direct screen. Streaming decoding supports split
UTF-8 and CP949 characters. The device still owns completion and prompt redraw;
the app does not synthesize a local editing line.

The raw RX observer only copies bytes into a queue capped at 1 MiB / 1,024
chunks. A UI timer drains at most 32 KiB per batch and awaits xterm's parse
acknowledgement before sending another batch. Overflow turns direct input off
with an error while normal RX logging continues. Old connection generations,
stopped sessions and stale browser session IDs cannot append to a new screen.

## Automated reproduction

`TerminalOutputBufferTests` sends the same RX chunks through the raw display
buffer and normal C# `LogPipeline`. It exports the actual decoded batches when
`SERIALMONITOR_TERMINAL_RX_FIXTURE` is set. The browser check consumes those
batches through the shipped `serialMonitorAppendDirectOutput` function, rather
than calling `terminal.write` with hand-written echo text.

```powershell
$env:SERIALMONITOR_TERMINAL_RX_FIXTURE = "$PWD/artifacts/direct-rx-fixture.json"
dotnet test SerialMonitor.WinUI.Tests/SerialMonitor.WinUI.Tests.csproj -p:Platform=x64 --filter 'FullyQualifiedName~TerminalInputSessionTests|FullyQualifiedName~TerminalOutputBufferTests'
# NODE_PATH may point to an existing Playwright installation.
node scripts/test_xterm_direct_input.cjs
node scripts/test_xterm_direct_rx.cjs
node scripts/test_xterm_command_shortcuts.cjs
```

Coverage includes device Backspace deletion, split ANSI cursor moves, CR/erase
history redraw, a prompt restored after an asynchronous log, LF without CR,
split multibyte decoding, stop/reconnect fences, bounded input/output queues,
and unchanged escaped normal-log snapshots after direct input ends.

Native GUI checks are performed by the user. These automated checks do not
claim validation of a particular physical device's shell or long-running UART
stability.
