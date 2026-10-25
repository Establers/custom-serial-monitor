using System.Text;
using System.Text.Json;
using SerialMonitor.Cli;
using SerialMonitor.Core.Automation;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    var command = args.FirstOrDefault() ?? "help";
    var flags = ParseFlags(args.Skip(1).ToArray());
    if (command == "mcp")
    {
        try
        {
            await new StdioMcpServer(flags.GetValueOrDefault("instance")).RunAsync(Console.In, Console.Out, stop.Token);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
    if (command is "help" or "--help" or "-h")
    {
        Console.WriteLine("""
        serialctl instances
        serialctl status|ports|commands [--instance INSTANCE_ID]
        serialctl connect --port COM7 [--baud 115200] [--instance INSTANCE_ID]
        serialctl disconnect [--instance INSTANCE_ID]
        serialctl request --text "status" [--ending crlf] [--duration-ms 2000] [--instance INSTANCE_ID]
        serialctl request --hex "AA 01 FF" [--duration-ms 2000] [--instance INSTANCE_ID]
        serialctl request --text-file command.txt [--ending none] [--duration-ms 2000] [--preview-bytes 4096]
        serialctl send --text "status" [--ending crlf] [--instance INSTANCE_ID]
        serialctl capture [--duration-ms 2000] [--instance INSTANCE_ID]
        serialctl read --capture CAPTURE_ID [--offset 0] [--limit 16384] [--format text|hex] [--instance INSTANCE_ID]
        serialctl cancel [--instance INSTANCE_ID]
        serialctl mcp [--instance INSTANCE_ID]

        All data commands print JSON. The GUI must be running; it owns the COM port.
        Captures include ALL new RX, without matching, from before TX until duration-ms after TX.
        Large results have page.hasMore=true; keep reading with page.nextOffset.
        Capture/send commands accept --preview-bytes 4..65536 (default 16384); this limits only the first returned page.
        --text-file reads a UTF-8 command file without shell interpolation; existing newlines are preserved.
        TX is never automatically retried. HEX mode sends only the supplied bytes.
        """);
        return 0;
    }
    var operation = command switch
    {
        "instances" => "list_instances", "commands" => "list_commands", "request" or "send-and-capture" => "send_and_capture",
        "capture" => "capture_rx", "read" => "read_capture", "cancel" => "cancel_capture", _ => command
    };
    var arguments = new Dictionary<string, object?>();
    void Add(string flag, string name, bool integer = false)
    {
        if (flags.TryGetValue(flag, out var value)) arguments[name] = integer ? int.Parse(value) : value;
    }
    Add("instance", "instanceId"); Add("port", "port"); Add("baud", "baudRate", true);
    Add("duration-ms", "durationMs", true); Add("ending", "lineEnding");
    Add("preview-bytes", "previewBytes", true);
    Add("capture", "captureId"); Add("offset", "offset", true); Add("limit", "limit", true); Add("format", "format");
    CliCommandInput.AddArguments(flags, arguments);
    stop.CancelAfter(TimeSpan.FromSeconds(90));
    var response = await AutomationToolRouter.CallAsync(operation, JsonSerializer.SerializeToElement(arguments, AutomationJson.Options), null, stop.Token);
    Console.WriteLine(JsonSerializer.Serialize(response, AutomationJson.Options));
    return !response.Success || (response.Data.ValueKind == JsonValueKind.Object && response.Data.TryGetProperty("capture", out var capture) &&
        capture.GetProperty("captureStatus").GetString() != "completed") ? 1 : 0;
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(AutomationResponse.Fail(ex is AutomationException a ? a.Code : "cli_error", ex.Message), AutomationJson.Options));
    return 1;
}

static Dictionary<string, string> ParseFlags(string[] flags)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    var allowed = new HashSet<string>(["instance", "port", "baud", "text", "text-file", "hex", "ending", "duration-ms", "preview-bytes", "capture", "offset", "limit", "format"]);
    for (var i = 0; i < flags.Length; i++)
    {
        if (flags[i] == "--json") continue;
        if (!flags[i].StartsWith("--") || !allowed.Contains(flags[i][2..]) || i + 1 >= flags.Length)
            throw new ArgumentException("Unknown or incomplete option: " + flags[i]);
        if (!result.TryAdd(flags[i][2..], flags[++i])) throw new ArgumentException("Duplicate option.");
    }
    return result;
}
