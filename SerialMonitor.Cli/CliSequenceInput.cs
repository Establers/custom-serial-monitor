using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Cli;

// Keep sequence file contents out of shell command construction, and reject
// misplaced options before routing any request to the GUI.
public static class CliSequenceInput
{
    public const int MaximumFileBytes = 48 * 1024;

    public static AutomationRequest Parse(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) throw new ArgumentException("Choose sequence list|get|save|delete|run|status|cancel.");
        var command = tokens[0];
        var supported = command switch
        {
            "list" => new[] { "instance", "offset", "limit" },
            "get" or "delete" => ["name", "instance"],
            "save" => ["file", "overwrite", "instance"],
            "run" => ["name", "port", "instance"],
            "status" => ["run", "instance"],
            "cancel" => ["run", "instance"],
            _ => throw new ArgumentException("Unknown sequence command: " + command)
        };
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < tokens.Count; i++)
        {
            if (tokens[i] == "--json") continue;
            if (!tokens[i].StartsWith("--", StringComparison.Ordinal) || !supported.Contains(tokens[i][2..]) ||
                i + 1 >= tokens.Count || tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Unknown, misplaced or incomplete sequence option: " + tokens[i]);
            if (!flags.TryAdd(tokens[i][2..], tokens[++i])) throw new ArgumentException("Duplicate sequence option.");
        }
        string Required(string name) => flags.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("sequence " + command + " requires --" + name + ".");

        var arguments = new Dictionary<string, object?>();
        if (flags.TryGetValue("instance", out var instance)) arguments["instanceId"] = instance;
        if (command == "list")
        {
            foreach (var key in new[] { "offset", "limit" })
            {
                if (!flags.TryGetValue(key, out var text)) continue;
                if (!int.TryParse(text, out var value) || value < (key == "limit" ? 1 : 0) || (key == "limit" && value > 100))
                    throw new ArgumentException("sequence list --offset must be >= 0 and --limit must be 1..100.");
                arguments[key] = value;
            }
        }
        if (command is "get" or "delete" or "run") arguments["name"] = Required("name");
        if (command == "cancel") arguments["runId"] = Required("run");
        if (command == "status" && flags.TryGetValue("run", out var run)) arguments["runId"] = run;
        if (flags.TryGetValue("port", out var port)) arguments["port"] = port;
        if (command == "save")
        {
            if (flags.TryGetValue("overwrite", out var overwrite))
            {
                if (overwrite is not ("true" or "false")) throw new ArgumentException("--overwrite must be true or false.");
                arguments["overwrite"] = overwrite == "true";
            }
            arguments["sequence"] = ReadSequenceFile(Required("file"));
        }
        return new AutomationRequest("sequence_" + (command == "run" ? "start" : command),
            JsonSerializer.SerializeToElement(arguments, AutomationJson.Options));
    }

    public static JsonElement ReadSequenceFile(string path)
    {
        // Bound the read even if another process grows the file while it is open.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[MaximumFileBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count == 0 || count > MaximumFileBytes)
            throw new ArgumentException("Sequence JSON file must contain 1..49152 UTF-8 bytes.");
        var start = count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        var json = new UTF8Encoding(false, true).GetString(bytes, start, count - start);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Sequence JSON must be one definition object, not an array or profile wrapper.");
        return document.RootElement.Clone();
    }
}
