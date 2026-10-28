namespace SerialMonitor.Cli;

internal static class CliUsage
{
    public const string Text = """
    serialctl describe [--json]
    serialctl instances
    serialctl status|ports|commands [--instance INSTANCE_ID]
    serialctl mode [--set terminal|hex] [--instance INSTANCE_ID]
    serialctl connect --port COM7 [--baud 115200] [--instance INSTANCE_ID]
    serialctl configure [--baud 115200] [--byte-timeout-ms 50] [--port COM7] [--instance INSTANCE_ID]
    serialctl disconnect [--instance INSTANCE_ID]
    serialctl request --text "status" [--ending crlf] [--duration-ms 2000] [--instance INSTANCE_ID]
    serialctl request --hex "AA 01 FF" [--duration-ms 2000] [--format hex] [--instance INSTANCE_ID]
    serialctl request --text-file command.txt [--ending none] [--duration-ms 2000] [--preview-bytes 4096]
    serialctl send --text "status" [--ending crlf] [--instance INSTANCE_ID]
    serialctl capture [--duration-ms 2000] [--format text|hex] [--instance INSTANCE_ID]
    serialctl read --capture CAPTURE_ID [--offset 0] [--limit 16384] [--format text|hex] [--instance INSTANCE_ID]
    serialctl cancel [--instance INSTANCE_ID]
    serialctl sequence list [--offset 0] [--limit 100] [--instance INSTANCE_ID]
    serialctl sequence get|delete --name NAME [--instance INSTANCE_ID]
    serialctl sequence save --file sequence.json [--overwrite true] [--instance INSTANCE_ID]
    serialctl sequence run --name NAME [--port COM7] [--instance INSTANCE_ID]
    serialctl sequence status [--run RUN_ID] [--instance INSTANCE_ID]
    serialctl sequence cancel --run RUN_ID [--instance INSTANCE_ID]
    serialctl mcp [--instance INSTANCE_ID]

    All data commands print JSON. describe needs no GUI; other control commands use the running GUI, which owns the COM port.
    Captures include ALL new RX, without matching, from before TX until duration-ms after TX.
    Large results have page.hasMore=true; keep reading with page.nextOffset.
    Capture/send commands accept --preview-bytes 4..65536 (default 16384); this limits only the first returned page.
    --text-file reads a UTF-8 command file without shell interpolation; existing newlines are preserved.
    mode reports or changes the GUI Terminal/HEX mode. A change clears visible logs and updates GUI manual TX interpretation.
    --hex/--text choose this command's TX encoding independently of GUI mode; HEX TX sends only the supplied bytes.
    HEX TX defaults its RX preview to hex; --format text|hex overrides the preview format for send/request/capture.
    configure requires a connected GUI port and applies baud/timeout live on the same connection without TX.
    --baud accepts 1..10000000, including custom rates when supported by the serial driver.
    --byte-timeout-ms (1..5000) controls the live HEX RX grouping gap, independently of duration-ms and raw RX capture.
    Windows/USB/RS-485 delivery can split or combine reads; allow margin above theoretical wire timing and tune from actual RX.
    TX is never automatically retried. Saved capture files are unaffected by GUI mode changes.
    sequence save registers a UTF-8 JSON definition (maximum 48 KiB) without sending; replacement requires --overwrite true.
    Register frequently used HEX or Terminal commands as named sequences with explicit txMode, lineEnding and captureMs.
    sequence run starts a background job and returns runId promptly. It continues after this CLI/MCP call disconnects.
    Inspect sequence status and read its saved capture in pages. Stop with sequence cancel --run RUN_ID.
    Optional sequence expect uses exact contiguous text/hex matching only; interleaved output can legitimately be not_matched.
    """;
}
