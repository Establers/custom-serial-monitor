# Direct terminal input and RX rendering

Direct input uses xterm keystrokes with no added line ending, and a separate,
generation-bound raw RX display buffer. The normal `LogPipeline` still receives
the same chunks and continues parsing, rule detection and asynchronous file
logging. Its escaped log text is not sent to the interactive screen.

Enabling direct input preserves visible logs and the cursor. Disabling it restores the
retained normal log snapshot. `CR`, `BS`, ANSI cursor/erase sequences and partial
prompts stay intact in the direct screen. Streaming decoding supports split
UTF-8 and CP949 characters. The device still owns completion and prompt redraw;
completion and history remain device-owned.

The leftmost 24-pixel terminal icon opens an ON/OFF menu with local echo.
Local echo defaults on for devices that do not echo keystrokes, and can be
turned off for shells with their own echo. Printed input appears immediately;
Enter still sends CR for the device to execute its accumulated command. Native
WebView2 focus is assigned before focusing/verifying xterm's textarea, after the
menu finishes closing.
The local display mirror is limited to 16,384 characters and 32 pending echo
chunks. It uses the bundled xterm Unicode widths and grapheme segmentation,
and redraws only its own span across wrapped rows. Empty/excess Backspace
cannot erase the prompt. Device RX and resize relinquish the local span;
out-of-viewport edits and display failures stop direct input with an error.
The mirror never buffers a command for TX or rewrites the bytes sent.

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
node scripts/test_xterm_local_echo.cjs
node scripts/test_xterm_command_shortcuts.cjs
```

Coverage includes device Backspace deletion, split ANSI cursor moves, CR/erase
history redraw, a prompt restored after an asynchronous log, LF without CR,
split multibyte decoding, stop/reconnect fences, bounded input/output queues,
and unchanged escaped normal-log snapshots after direct input ends.
Browser input checks also cover preserving existing logs on enable, printable
input/Backspace/Enter without remote echo, turning local echo off, and restoring
textarea focus after an unrelated menu/button has focus.
Local display regressions include prompt boundaries, excess deletion, middle
insertion, wide glyphs, split combining input, wrapped deletion/navigation and
Ctrl+C at a full final row.

Native GUI checks are performed by the user. These automated checks do not
claim validation of a particular physical device's shell or long-running UART
stability.
