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
    var flags = command == "sequence" ? new Dictionary<string, string>() : ParseFlags(args.Skip(1).ToArray());
    if (flags.ContainsKey("set") && command != "mode")
        throw new ArgumentException("--set is only supported by serialctl mode.");
    if (flags.ContainsKey("byte-timeout-ms") && command != "configure")
        throw new ArgumentException("--byte-timeout-ms is only supported by serialctl configure.");
    if (command == "configure")
    {
        if (flags.Keys.Any(key => key is not ("instance" or "port" or "baud" or "byte-timeout-ms")))
            throw new ArgumentException("configure accepts only --baud, --byte-timeout-ms, --port and --instance.");
        if (!flags.ContainsKey("baud") && !flags.ContainsKey("byte-timeout-ms"))
            throw new ArgumentException("configure requires --baud and/or --byte-timeout-ms. Use status to read current settings.");
        if (flags.TryGetValue("baud", out var baud) && (!int.TryParse(baud, out var baudRate) || baudRate is < 1 or > 10000000))
            throw new ArgumentException("--baud must be an integer between 1 and 10000000. The serial driver must support the requested rate.");
        if (flags.TryGetValue("byte-timeout-ms", out var timeout) &&
            (!int.TryParse(timeout, out var timeoutMs) || timeoutMs is < 1 or > 5000))
            throw new ArgumentException("--byte-timeout-ms must be an integer between 1 and 5000.");
    }
    if (command == "describe")
    {
        if (flags.Count != 0)
            throw new ArgumentException("describe takes no options except --json and does not select or control a GUI.");
        Console.WriteLine(JsonSerializer.Serialize(AutomationResponse.Ok(ControlCapabilities.Describe()), AutomationJson.Options));
        return 0;
    }
    if (command == "mcp")
    {
        try
        {
            await new StdioMcpServer(flags.GetValueOrDefault("instance")).RunAsync(Console.In, Console.Out, stop.Token);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
    if (command is "help" or "--help" or "-h" ||
        (command == "sequence" && args.Length == 2 && args[1] is "help" or "--help" or "-h"))
    {
        Console.WriteLine(CliUsage.Text);
        return 0;
    }
    var operation = command switch
    {
        "instances" => "list_instances", "commands" => "list_commands", "request" or "send-and-capture" => "send_and_capture",
        "capture" => "capture_rx", "read" => "read_capture", "cancel" => "cancel_capture",
        "mode" => flags.ContainsKey("set") ? "set_mode" : "status", _ => command
    };
    var arguments = new Dictionary<string, object?>();
    void Add(string flag, string name, bool integer = false)
    {
        if (flags.TryGetValue(flag, out var value)) arguments[name] = integer ? int.Parse(value) : value;
    }
    Add("instance", "instanceId"); Add("port", "port"); Add("baud", "baudRate", true);
    Add("byte-timeout-ms", "byteTimeoutMs", true);
    Add("duration-ms", "durationMs", true); Add("ending", "lineEnding");
    Add("preview-bytes", "previewBytes", true);
    Add("set", "mode");
    Add("capture", "captureId"); Add("offset", "offset", true); Add("limit", "limit", true); Add("format", "format");
    CliCommandInput.AddArguments(flags, arguments);
    var request = command == "sequence" ? CliSequenceInput.Parse(args.Skip(1).ToArray()) :
        new AutomationRequest(operation, JsonSerializer.SerializeToElement(arguments, AutomationJson.Options));
    stop.CancelAfter(TimeSpan.FromSeconds(90));
    var response = await AutomationToolRouter.CallAsync(request.Operation, request.Arguments, null, stop.Token);
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
    var allowed = new HashSet<string>(["instance", "port", "baud", "byte-timeout-ms", "text", "text-file", "hex", "ending", "duration-ms", "preview-bytes", "capture", "offset", "limit", "format", "set"]);
    for (var i = 0; i < flags.Length; i++)
    {
        if (flags[i] == "--json") continue;
        if (!flags[i].StartsWith("--") || !allowed.Contains(flags[i][2..]) || i + 1 >= flags.Length)
            throw new ArgumentException("Unknown or incomplete option: " + flags[i]);
        if (!result.TryAdd(flags[i][2..], flags[++i])) throw new ArgumentException("Duplicate option.");
    }
    return result;
}
