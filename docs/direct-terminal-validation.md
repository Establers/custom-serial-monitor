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

The rightmost fixed 24-pixel terminal icon opens a right-aligned ON/OFF menu with local echo.
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
For example, typing `STOX`, Backspace, `P`, Enter displays `STOP` but sends
`53 54 4F 58 7F 50 0D`. Older lab firmware rejects that control-containing line;
plain `53 54 4F 50 0D` returns `OK STOP`. Updated lab firmware handles BS/DEL,
Ctrl+C and command Tab completion in LAB text mode. Use the TX editor for a
line-only device without terminal editing. A real shell must support terminal editing controls.

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

## Retained TX, rendered-screen search and sequences

Direct TX still sends each key immediately and feeds the original file/event/
protocol evidence once. A bounded 4,096-character accumulator separately coalesces
successful writes into retained TX rows on Enter, Ctrl+C, size limit or session
end. OFF flushes even an unsubmitted fragment after the worker stops. Control
bytes are escaped (`\b`, `\x7F`, `\t`, `\x1B`); these rows describe actual
sent keys rather than guessing the remote shell's completed command. No TX rows
are injected into the live cursor-owned screen. Unsent queued input is excluded.

ON search captures the active xterm screen and scrollback after pending render
writes. Wrapped rows form logical lines; ANSI redraw/erase, local echo, tab cells,
wide/combining characters and alternate-screen contents are read from rendered
cells. The existing host search engine supplies counts, result pages, case/word/
regex semantics and offsets. Session/snapshot IDs and markers map the same match
back to its cells for highlighting. Changed/evicted lines fail selection instead
of selecting another duplicate. Results remain manual snapshots, marked stale
when the screen changes; Search/Enter/Refresh captures again. ON/OFF resets the
search snapshot so counts from different views are never mixed. Capture is bounded
to 16 Mi characters; an oversize snapshot reports an error instead of truncating.

A GUI sequence keeps the same direct session, raw RX screen and scrollback. While
running (including paused/delay/RX-check phases), native and browser input gates
reject typing and paste. Previously accepted writes drain through the ordered
queue before sequence TX; keyboard input resumes in the same session on completion,
stop or error. A partially entered command must first be finished with Enter or
canceled with Ctrl+C; the app never silently executes or deletes it. Disconnect,
HEX mode, view pause and bridge/automation reservations retain their prior OFF rules.

Validation: `TerminalTransmitHistoryTests`, `TerminalSearchContentTests` and
`scripts/test_xterm_direct_search.cjs`. Set
`SERIALMONITOR_TERMINAL_SEARCH_FIXTURE` to a JSON path; run the browser script,
run `dotnet test` with the same variable, then rerun the browser script. It exports
actual screen snapshots, runs the real C# matcher, then replays every resulting
highlight request in the shipped terminal. No native GUI or physical UART is used.
